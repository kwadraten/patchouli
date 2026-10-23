namespace BuiltInMetadata

open System
open System.Collections.Generic
open System.Globalization
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
open Patchouli.Core.Bibliography.MetadataLookup
open Patchouli.Core.Results
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk

/// <summary>
/// Shared helpers for the built-in metadata FSX sources: JSON/XML/regex parsing ported from the
/// previous in-process sources, plus a uniform HTTP fetch that maps provider statuses onto the
/// shared metadata error vocabulary. Every built-in metadata main.fsx #loads this file.
/// </summary>
module Support =
    let authorRole = "author"
    let issuedRole = "issued"

    /// Copies a parsed candidate that used an empty source id onto the owning extension source.
    let withSource (sourceId: string) (candidate: MetadataCandidate) =
        MetadataCandidate(sourceId, candidate.Title, candidate.Subtitle, candidate.TitleShort, candidate.Creators,
            candidate.Dates, candidate.PublicationTitle, candidate.ContainerTitleShort, candidate.CollectionTitle,
            candidate.Publisher, candidate.Place, candidate.Edition, candidate.Genre, candidate.Number,
            candidate.ChapterNumber, candidate.Volume, candidate.Version, candidate.Issue, candidate.Pages,
            candidate.Language, candidate.Status, candidate.Note, candidate.Abstract, candidate.Tags,
            candidate.Identifiers, candidate.SuggestedItemType, candidate.TypeConfidence)

    /// Mirrors the previous host rule: no title and no identifiers means an invalid provider response.
    let isEmptyCandidate (candidate: MetadataCandidate) =
        let title = candidate.Title
        (isNull title || String.IsNullOrWhiteSpace title) &&
        (match candidate.Identifiers with
         | null -> true
         | identifiers -> identifiers.Count = 0)

    /// Non-null error code for re-propagating a failed helper result.
    let failureCode (result: Result<'T>) =
        match result.ErrorCode with
        | null -> MetadataLookupErrorCodes.ProviderUnavailable
        | code -> code

    /// Non-null error message for re-propagating a failed helper result.
    let failureMessage (result: Result<'T>) =
        match result.ErrorMessage with
        | null -> "The metadata source request failed."
        | message -> message

    // ----- HTTP -----------------------------------------------------------------

    /// Pure request constructors shared by metadata sources; these do not capture run resources.
    let getRequest (uri: Uri) (accept: string) =
        let request = new HttpRequestMessage(HttpMethod.Get, uri)
        request.Headers.Accept.ParseAdd(accept)
        request

    let postJsonRequest (uri: Uri) (body: string) =
        let request = new HttpRequestMessage(HttpMethod.Post, uri)
        request.Headers.Accept.ParseAdd("application/json")
        request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        request

    let private quotaUnavailableBody (body: string) =
        body.Contains("quota_limit_value\": \"0", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("Queries per day", StringComparison.OrdinalIgnoreCase)

    /// Fetches one metadata document and maps provider statuses onto the shared error codes.
    let fetchSource (resources: IExtensionResources) (http: HttpClient) (name: string)
                    (accept: string) (uri: Uri) (cancellationToken: CancellationToken) = task {
        do! resources.WaitToProceedAsync(cancellationToken)
        use request = getRequest uri accept
        use! response = http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
        let statusCode = int response.StatusCode
        if statusCode = 404 || statusCode = 204 then
            return Result<string>.Failure(MetadataLookupErrorCodes.NotFound,
                name + " did not find a matching record.")
        elif statusCode = 429 then
            let! body = response.Content.ReadAsStringAsync(cancellationToken)
            if quotaUnavailableBody body then
                return Result<string>.Failure(MetadataLookupErrorCodes.QuotaUnavailable,
                    name + " anonymous API quota is unavailable for this client; retrying will not help until the provider quota changes.")
            else
                return Result<string>.Failure(MetadataLookupErrorCodes.RateLimited,
                    name + " rate limit was reached.")
        elif not response.IsSuccessStatusCode then
            return Result<string>.Failure(MetadataLookupErrorCodes.ProviderUnavailable,
                "The metadata source returned HTTP " + string statusCode + ".")
        else
            let! body = response.Content.ReadAsStringAsync(cancellationToken)
            return Result<string>.Success(body)
    }

    // ----- JSON primitives ------------------------------------------------------

    /// Trims a possibly-null provider value into an option; blank values are absent.
    let clean (value: string | null) =
        match value with
        | null -> None
        | text when String.IsNullOrWhiteSpace text -> None
        | text -> Some (text.Trim())

    let jsonScalar (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.String -> clean (value.GetString())
        | JsonValueKind.Number -> Some (value.GetRawText())
        | _ -> None

    let jsonString (element: JsonElement) (property: string) =
        let mutable value = JsonElement()
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(property, &value) then
            jsonScalar value
        else None

    let jsonChildString (element: JsonElement) (parent: string) (property: string) =
        let mutable value = JsonElement()
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(parent, &value) then
            jsonString value property
        else None

    let jsonStrings (element: JsonElement) (property: string) =
        let mutable value = JsonElement()
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(property, &value) then
            match value.ValueKind with
            | JsonValueKind.Array -> value.EnumerateArray() |> Seq.choose jsonScalar |> Seq.toArray
            | _ -> match jsonScalar value with Some one -> [| one |] | None -> Array.empty
        else Array.empty

    let jsonFirstString (element: JsonElement) (property: string) =
        jsonStrings element property |> Array.tryHead

    let tryParseInt (text: string) =
        let mutable parsed = 0
        if Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, &parsed) then Some parsed else None

    let jsonInt (element: JsonElement) (property: string) =
        jsonString element property |> Option.bind tryParseInt

    let lastPath (value: string) =
        value.TrimEnd('/').Split('/') |> Array.last

    let identifierFields (element: JsonElement) (fields: (string * string) list) =
        let result = ResizeArray<MetadataIdentifier>()
        if element.ValueKind = JsonValueKind.Object then
            for (property, scheme) in fields do
                let mutable value = JsonElement()
                if element.TryGetProperty(property, &value) then
                    let values =
                        match value.ValueKind with
                        | JsonValueKind.Array -> value.EnumerateArray() |> Seq.choose jsonScalar
                        | _ -> match jsonScalar value with Some one -> Seq.singleton one | None -> Seq.empty
                    for item in values do
                        match clean item with
                        | Some text -> result.Add(MetadataIdentifier(scheme, text))
                        | None -> ()
        result.ToArray()

    let jsonPerson (person: JsonElement) =
        let family = match jsonString person "familyName" with Some value -> Some value | None -> jsonString person "family"
        let given = match jsonString person "givenName" with Some value -> Some value | None -> jsonString person "given"
        let literal = jsonString person "name"
        match family, given, literal with
        | None, None, None -> None
        | _ -> Some (MetadataCreator(authorRole, Option.toObj family, Option.toObj given, Option.toObj literal))

    let jsonPeople (element: JsonElement) (property: string) =
        let mutable list = JsonElement()
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(property, &list) &&
           list.ValueKind = JsonValueKind.Array then
            list.EnumerateArray() |> Seq.choose jsonPerson |> Seq.toArray
        else Array.empty

    let jsonLiteralPeople (value: string) =
        if String.IsNullOrWhiteSpace value then Array.empty
        else
            value.Split([| ';'; '／' |], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.map (fun name -> MetadataCreator(authorRole, Literal = name))

    let jsonLdPeople (value: JsonElement) =
        let values =
            if value.ValueKind = JsonValueKind.Array then value.EnumerateArray() |> Seq.map id
            else Seq.singleton value
        values |> Seq.map (fun item ->
            MetadataCreator(authorRole, Option.toObj (jsonString item "familyName"),
                Option.toObj (jsonString item "givenName"), Option.toObj (jsonString item "name"))) |> Seq.toArray

    let objectValues (element: JsonElement) (property: string) (child: string) =
        let mutable values = JsonElement()
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(property, &values) &&
           values.ValueKind = JsonValueKind.Array then
            values.EnumerateArray() |> Seq.choose (fun item -> jsonString item child) |> Seq.toArray
        else Array.empty

    let objectNames (element: JsonElement) (property: string) (role: string) =
        objectValues element property "name" |> Array.map (fun name -> MetadataCreator(role, Literal = name))

    let literalDate (value: string) =
        match clean value with
        | Some text -> [| MetadataDate(issuedRole, Literal = text) |]
        | None -> Array.empty

    let dateParts (element: JsonElement) (properties: string list) =
        let mutable found = None
        let mutable index = 0
        while found.IsNone && index < properties.Length do
            let mutable date = JsonElement()
            let mutable outer = JsonElement()
            if element.ValueKind = JsonValueKind.Object &&
               element.TryGetProperty(properties[index], &date) &&
               date.TryGetProperty("date-parts", &outer) && outer.ValueKind = JsonValueKind.Array then
                let parts = outer.EnumerateArray() |> Seq.tryHead
                match parts with
                | Some inner when inner.ValueKind = JsonValueKind.Array ->
                    let values =
                        inner.EnumerateArray() |> Seq.choose (fun part ->
                            let mutable integer = 0
                            if part.TryGetInt32(&integer) then Some integer else None) |> Seq.toArray
                    if values.Length > 0 then found <- Some [| MetadataDate(issuedRole, values) |]
                | _ -> ()
            index <- index + 1
        match found with Some dates -> dates | None -> Array.empty

    let pageRange (element: JsonElement) =
        let first = jsonChildString element "biblio" "first_page"
        let last = jsonChildString element "biblio" "last_page"
        match first with
        | None -> None
        | Some value when last.IsNone || last.Value = value -> Some value
        | Some value -> Some (value + "-" + last.Value)

    let mapType (value: string) =
        let normalized =
            match clean value with
            | Some text -> text.ToLowerInvariant()
            | None -> String.Empty
        match normalized with
        | "journal-article" | "article" | "article-journal" -> Some "article-journal"
        | "book" | "monograph" -> Some "book"
        | "book-chapter" -> Some "chapter"
        | "proceedings-article" | "conference" | "conference-paper" -> Some "paper-conference"
        | "dissertation" | "thesis" -> Some "thesis"
        | "report" -> Some "report"
        | "posted-content" | "preprint" -> Some "manuscript"
        | _ -> None

    // ----- XML primitives -------------------------------------------------------

    let xText (root: XContainer) (name: string) =
        let element = root.Descendants() |> Seq.tryFind (fun item -> item.Name.LocalName = name)
        match element with
        | Some item ->
            let value = item.Value.Trim()
            if value.Length > 0 then Some value else None
        | None -> None

    let xNumber (root: XContainer) (name: string) =
        xText root name |> Option.bind tryParseInt

    let xMonth (root: XContainer) =
        match xNumber root "Month" with
        | Some number -> Some number
        | None ->
            match xText root "Month" with
            | Some value when value.Length >= 3 ->
                let names = DateTimeFormatInfo.InvariantInfo.AbbreviatedMonthNames
                let mutable index = 0
                let mutable result = None
                while index < names.Length && result.IsNone do
                    if String.Equals(names[index], value.Substring(0, 3), StringComparison.OrdinalIgnoreCase) then
                        result <- Some (index + 1)
                    index <- index + 1
                result
            | _ -> None

    let private xFirst (record: XElement) (name: string) =
        record.Descendants()
        |> Seq.tryFind (fun item -> String.Equals(item.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
        |> Option.bind (fun item -> clean item.Value)

    let private xAll (record: XElement) (name: string) =
        record.Descendants()
        |> Seq.filter (fun item -> String.Equals(item.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
        |> Seq.choose (fun item -> clean item.Value)
        |> Seq.distinctBy (fun value -> value.ToLowerInvariant())
        |> Seq.toArray

    let private identifierMatches (rawValue: string) (requested: NormalizedIdentifier) =
        let normalized = IdentifierNormalizer.Normalize(requested.Scheme, rawValue)
        normalized.IsSuccess &&
        String.Equals(normalized.Value.Value, requested.Value, StringComparison.OrdinalIgnoreCase)

    /// Parses NDL Search OpenSearch RSS/Atom (Dublin Core) responses.
    let parseDublinCore (document: XDocument) (requested: NormalizedIdentifier) =
        let identifierTypes =
            match requested.Scheme with
            | "ndlbibid" -> [| "NDLBibID" |]
            | "jpno" -> [| "JPNO" |]
            | "isbn" -> [| "ISBN"; "ISBN13" |]
            | _ -> Array.empty
        let isRecord (element: XElement) =
            match element.Name.LocalName with
            | "item" | "recordData" -> true
            | _ -> false
        let matchesRequested (element: XElement) =
            element.Descendants()
            |> Seq.exists (fun identifier ->
                identifier.Name.LocalName = "identifier" &&
                identifierMatches (identifier.Value.Trim()) requested &&
                identifier.Attributes()
                |> Seq.exists (fun attribute ->
                    attribute.Name.LocalName = "type" &&
                    identifierTypes |> Array.exists (fun kind ->
                        attribute.Value.EndsWith(kind, StringComparison.OrdinalIgnoreCase))))
        let hasRecords = document.Descendants() |> Seq.exists isRecord
        let record =
            document.Descendants()
            |> Seq.filter isRecord
            |> Seq.tryFind matchesRequested
            |> Option.orElse (if hasRecords then None else (match document.Root with null -> None | root -> Some root))
        match record with
        | None -> None
        | Some element ->
            let identifiers =
                element.Descendants()
                |> Seq.filter (fun item -> item.Name.LocalName = "identifier")
                |> Seq.choose (fun item ->
                    let value = item.Value.Trim()
                    let kind =
                        item.Attributes()
                        |> Seq.tryFind (fun attribute -> attribute.Name.LocalName = "type")
                        |> Option.map (fun attribute -> attribute.Value)
                    match kind with
                    | Some text when text.EndsWith("NDLBibID", StringComparison.OrdinalIgnoreCase) ||
                                     value.Contains("id.ndl.go.jp/bib/", StringComparison.OrdinalIgnoreCase) ->
                        Some (MetadataIdentifier("ndlbibid", lastPath (value.TrimEnd('/'))))
                    | Some text when text.EndsWith("JPNO", StringComparison.OrdinalIgnoreCase) ||
                                     value.Contains("id.ndl.go.jp/jpno/", StringComparison.OrdinalIgnoreCase) ->
                        Some (MetadataIdentifier("jpno", lastPath (value.TrimEnd('/'))))
                    | Some text when text.EndsWith("ISBN", StringComparison.OrdinalIgnoreCase) ||
                                     text.EndsWith("ISBN13", StringComparison.OrdinalIgnoreCase) ->
                        Some (MetadataIdentifier("isbn", value))
                    | Some text when text.EndsWith("NIIBibID", StringComparison.OrdinalIgnoreCase) ->
                        Some (MetadataIdentifier("ncid", value))
                    | _ -> None)
                |> Seq.append (Seq.singleton (MetadataIdentifier(requested.Scheme, requested.Value)))
                |> Seq.distinctBy (fun identifier -> (identifier.Scheme + "\u0000" + identifier.Value).ToLowerInvariant())
                |> Seq.toArray
            Some (MetadataCandidate("",
                Option.toObj (xFirst element "title"),
                Creators = (xAll element "creator" |> Array.map (fun name -> MetadataCreator(authorRole, Literal = name))),
                Dates = (match xFirst element "date" with Some date -> [| MetadataDate(issuedRole, Literal = date) |] | None -> Array.empty),
                Publisher = Option.toObj (xFirst element "publisher"),
                Language = Option.toObj (xFirst element "language"),
                Abstract = Option.toObj (xFirst element "description"),
                Tags = xAll element "subject",
                Identifiers = identifiers,
                SuggestedItemType = "book",
                TypeConfidence = 0.9))

    /// Parses the PubMed efetch XML response.
    let parsePubMed (document: XDocument) =
        let article = document.Descendants() |> Seq.tryFind (fun item -> item.Name.LocalName = "PubmedArticle")
        match article with
        | None -> None
        | Some articleElement ->
            let journal = articleElement.Descendants() |> Seq.tryFind (fun item -> item.Name.LocalName = "Journal")
            let ids =
                articleElement.Descendants()
                |> Seq.filter (fun item -> item.Name.LocalName = "ArticleId")
                |> Seq.choose (fun item ->
                    let kind =
                        match (match item.Attribute(XName.Get "IdType") with null -> None | attribute -> Some attribute.Value) with
                        | Some "doi" -> Some "doi"
                        | Some "pubmed" -> Some "pmid"
                        | Some "pmc" -> Some "pmcid"
                        | _ -> None
                    match kind with
                    | Some scheme -> Some (MetadataIdentifier(scheme, item.Value))
                    | None -> None)
                |> Seq.toArray
            let authors =
                articleElement.Descendants()
                |> Seq.filter (fun item -> item.Name.LocalName = "Author")
                |> Seq.map (fun item ->
                    MetadataCreator(authorRole, Option.toObj (xText item "LastName"),
                        Option.toObj (xText item "ForeName"), Option.toObj (xText item "CollectiveName")))
                |> Seq.toArray
            let date = journal |> Option.bind (fun container -> container.Descendants() |> Seq.tryFind (fun item -> item.Name.LocalName = "PubDate"))
            let year = date |> Option.bind (fun container -> xNumber container "Year")
            let month = date |> Option.bind xMonth
            let day = if month.IsSome then date |> Option.bind (fun container -> xNumber container "Day") else None
            let parts =
                [| match year with Some value -> yield value | None -> ()
                   match month with Some value -> yield value | None -> ()
                   match day with Some value -> yield value | None -> () |]
            let journalTitle = journal |> Option.bind (fun container -> xText container "Title")
            let abstractText =
                articleElement.Descendants()
                |> Seq.filter (fun item -> item.Name.LocalName = "AbstractText")
                |> Seq.choose (fun item -> clean item.Value)
                |> String.concat " "
            Some (MetadataCandidate("",
                Option.toObj (xText articleElement "ArticleTitle"),
                Creators = authors,
                Dates = (if parts.Length = 0 then Array.empty else [| MetadataDate(issuedRole, parts) |]),
                PublicationTitle = Option.toObj journalTitle,
                Volume = (match journal with Some container -> Option.toObj (xText container "Volume") | None -> null),
                Issue = (match journal with Some container -> Option.toObj (xText container "Issue") | None -> null),
                Pages = Option.toObj (xText articleElement "MedlinePgn"),
                Language = Option.toObj (xText articleElement "Language"),
                Abstract = (if String.IsNullOrWhiteSpace abstractText then null else abstractText),
                Tags = (articleElement.Descendants()
                        |> Seq.filter (fun item -> item.Name.LocalName = "Keyword")
                        |> Seq.choose (fun item -> clean item.Value) |> Seq.toArray),
                Identifiers = ids,
                SuggestedItemType = "article-journal",
                TypeConfidence = 0.98))

    /// Parses the arXiv Atom response.
    let parseArXiv (document: XDocument) =
        let entry = document.Descendants() |> Seq.tryFind (fun item -> item.Name.LocalName = "entry")
        match entry with
        | None -> None
        | Some element ->
            let id = (xText element "id") |> Option.map (fun value -> value.Split('/') |> Array.last)
            let doi = xText element "doi"
            let identifiers = ResizeArray<MetadataIdentifier>()
            match id with Some value -> identifiers.Add(MetadataIdentifier("arxiv", value)) | None -> ()
            match doi with Some value -> identifiers.Add(MetadataIdentifier("doi", value)) | None -> ()
            Some (MetadataCandidate("",
                Option.toObj (xText element "title"),
                Creators = (element.Elements()
                            |> Seq.filter (fun item -> item.Name.LocalName = "author")
                            |> Seq.map (fun item -> MetadataCreator(authorRole, Literal = Option.defaultValue "" (xText item "name")))
                            |> Seq.toArray),
                Dates = [| MetadataDate(issuedRole, Literal = Option.defaultValue "" (xText element "published")) |],
                Abstract = Option.toObj (xText element "summary"),
                Tags = (element.Elements()
                        |> Seq.filter (fun item -> item.Name.LocalName = "category")
                        |> Seq.choose (fun item -> match item.Attribute(XName.Get "term") with null -> None | attribute -> Some attribute.Value)
                        |> Seq.toArray),
                Identifiers = identifiers.ToArray(),
                SuggestedItemType = "manuscript",
                TypeConfidence = 0.95))

    /// Parses MARC XML (MARC21 or INTERMARC exchange) SRU responses for DnB and BnF.
    let parseMarc (document: XDocument) =
        let record =
            document.Descendants()
            |> Seq.tryFind (fun item ->
                item.Name.LocalName = "record" &&
                item.Descendants() |> Seq.exists (fun child -> match child.Name.LocalName with "datafield" | "controlfield" -> true | _ -> false))
        match record with
        | None -> None
        | Some element ->
            let sub (tag: string) (codes: string list) =
                element.Descendants()
                |> Seq.filter (fun item ->
                    item.Name.LocalName = "datafield" &&
                    (match item.Attribute(XName.Get "tag") with null -> None | attribute -> Some attribute.Value) = Some tag)
                |> Seq.collect (fun field ->
                    field.Elements()
                    |> Seq.filter (fun child ->
                        child.Name.LocalName = "subfield" &&
                        (match child.Attribute(XName.Get "code") with null -> None | attribute -> Some attribute.Value)
                        |> Option.exists (fun code -> List.contains code codes)))
                |> Seq.choose (fun child -> clean (child.Value.Trim().TrimEnd('/', ':', ';')))
                |> Seq.tryHead
            let authors =
                element.Descendants()
                |> Seq.filter (fun item ->
                    item.Name.LocalName = "datafield" &&
                    (match item.Attribute(XName.Get "tag") with null -> None | attribute -> Some attribute.Value)
                    |> Option.exists (fun tag -> List.contains tag [ "100"; "110"; "700"; "701"; "800" ]))
                |> Seq.choose (fun field ->
                    field.Elements()
                    |> Seq.tryFind (fun child ->
                        child.Name.LocalName = "subfield" &&
                        (match child.Attribute(XName.Get "code") with null -> None | attribute -> Some attribute.Value) = Some "a")
                    |> Option.bind (fun child -> clean child.Value))
                |> Seq.map (fun name -> MetadataCreator(authorRole, Literal = name))
                |> Seq.toArray
            let title = sub "245" [ "a" ] |> Option.orElse (sub "200" [ "a" ]) |> Option.orElse (sub "331" [ "a" ])
            let subtitle = sub "245" [ "b" ] |> Option.orElse (sub "200" [ "e" ]) |> Option.orElse (sub "335" [ "a" ])
            let date = sub "264" [ "c" ] |> Option.orElse (sub "260" [ "c" ]) |> Option.orElse (sub "210" [ "d" ]) |> Option.orElse (sub "419" [ "c" ])
            let isbn = sub "020" [ "a" ] |> Option.orElse (sub "010" [ "a" ]) |> Option.orElse (sub "540" [ "a" ])
            Some (MetadataCandidate("",
                Option.toObj title,
                Option.toObj subtitle,
                Creators = authors,
                Dates = (match date with Some value -> [| MetadataDate(issuedRole, Literal = value) |] | None -> Array.empty),
                Publisher = (sub "264" [ "b" ] |> Option.orElse (sub "260" [ "b" ]) |> Option.orElse (sub "210" [ "c" ]) |> Option.orElse (sub "419" [ "b" ]) |> Option.toObj),
                Place = (sub "264" [ "a" ] |> Option.orElse (sub "260" [ "a" ]) |> Option.orElse (sub "210" [ "a" ]) |> Option.toObj),
                Edition = (sub "250" [ "a" ] |> Option.orElse (sub "205" [ "a" ]) |> Option.orElse (sub "403" [ "a" ]) |> Option.toObj),
                Language = (sub "041" [ "a" ] |> Option.orElse (sub "101" [ "a" ]) |> Option.toObj),
                Identifiers = (match isbn with Some value -> [| MetadataIdentifier("isbn", value) |] | None -> Array.empty),
                SuggestedItemType = "book",
                TypeConfidence = 0.9))

    // ----- CALIS MARC (JSON) ----------------------------------------------------

    let private calisSubfields (entries: JsonElement array) (tag: string) (code: string) =
        entries
        |> Seq.filter (fun entry -> entry.ValueKind = JsonValueKind.Object &&
                                    (let mutable ignored = JsonElement()
                                     entry.TryGetProperty(tag, &ignored)))
        |> Seq.collect (fun entry ->
            let mutable field = JsonElement()
            if entry.TryGetProperty(tag, &field) then
                let mutable values = JsonElement()
                if field.ValueKind = JsonValueKind.Object && field.TryGetProperty("subfields", &values) &&
                   values.ValueKind = JsonValueKind.Array then values.EnumerateArray() |> Seq.map id
                else Seq.empty
            else Seq.empty)
        |> Seq.choose (fun value ->
            let mutable field = JsonElement()
            if value.ValueKind = JsonValueKind.Object && value.TryGetProperty(code, &field) then
                match field.GetString() with null -> None | text -> clean text
            else None)
        |> Seq.toArray

    /// Parses a CALIS MARC JSON detail record and requires an exact ISBN match.
    let parseCalisMarc (root: JsonElement) (requested: NormalizedIdentifier) (rid: string) =
        let mutable fields = JsonElement()
        if not (root.ValueKind = JsonValueKind.Object && root.TryGetProperty("fields", &fields) &&
                fields.ValueKind = JsonValueKind.Array) then
            None
        else
            let entries = fields.EnumerateArray() |> Seq.toArray
            let isbn =
                calisSubfields entries "010" "a"
                |> Array.tryFind (fun value ->
                    let normalized = IdentifierNormalizer.Normalize("isbn", value)
                    normalized.IsSuccess && normalized.Value.Value = requested.Value)
            match isbn with
            | None -> None
            | Some matchedIsbn ->
                let title = calisSubfields entries "200" "a" |> Array.tryHead
                match title with
                | None -> None
                | Some matchedTitle ->
                    let authors = ResizeArray<MetadataCreator>()
                    for tag in [ "701"; "702" ] do
                        for entry in entries |> Array.filter (fun item ->
                            let mutable ignored = JsonElement()
                            item.ValueKind = JsonValueKind.Object && item.TryGetProperty(tag, &ignored)) do
                            let mutable field = JsonElement()
                            if entry.TryGetProperty(tag, &field) then
                                let mutable subfields = JsonElement()
                                if field.ValueKind = JsonValueKind.Object && field.TryGetProperty("subfields", &subfields) &&
                                   subfields.ValueKind = JsonValueKind.Array then
                                    let values = subfields.EnumerateArray() |> Seq.toArray
                                    let name =
                                        values |> Array.tryPick (fun value ->
                                            let mutable child = JsonElement()
                                            if value.TryGetProperty("a", &child) then
                                                match child.GetString() with null -> None | text -> clean text
                                            else None)
                                    let role =
                                        values |> Array.tryPick (fun value ->
                                            let mutable child = JsonElement()
                                            if value.TryGetProperty("4", &child) then
                                                match child.GetString() with null -> None | text -> clean text
                                            else None)
                                    match name with
                                    | Some value ->
                                        authors.Add(MetadataCreator(
                                            (if role = Some "译" then "translator" else authorRole), Literal = value.Trim()))
                                    | None -> ()
                    let year =
                        calisSubfields entries "210" "d"
                        |> Array.choose (fun value ->
                            let matched = Regex.Match(value, "\d{4}")
                            if matched.Success then Some matched.Value else None)
                        |> Array.tryFind (fun value -> value.Length = 4)
                    let subjects =
                        Array.append (calisSubfields entries "606" "a") (calisSubfields entries "690" "a")
                        |> Seq.distinctBy (fun value -> value.ToLowerInvariant())
                        |> Seq.toArray
                    Some (MetadataCandidate("",
                        matchedTitle,
                        Option.toObj (calisSubfields entries "200" "e" |> Array.tryHead),
                        Creators = authors.ToArray(),
                        Dates = (match year with Some value -> [| MetadataDate(issuedRole, [| Int32.Parse(value, CultureInfo.InvariantCulture) |]) |] | None -> Array.empty),
                        Publisher = (calisSubfields entries "210" "c" |> Array.tryHead |> Option.toObj),
                        Place = (calisSubfields entries "210" "a" |> Array.tryHead |> Option.toObj),
                        CollectionTitle = (calisSubfields entries "225" "a" |> Array.tryHead |> Option.toObj),
                        Pages = (calisSubfields entries "215" "a" |> Array.tryHead |> Option.toObj),
                        Language = (calisSubfields entries "101" "a" |> Array.tryHead |> Option.toObj),
                        Abstract = (calisSubfields entries "330" "a" |> Array.tryHead |> Option.toObj),
                        Tags = subjects,
                        Identifiers = [| MetadataIdentifier("isbn", matchedIsbn); MetadataIdentifier("calis", rid) |],
                        SuggestedItemType = "book",
                        TypeConfidence = 0.98))

    // ----- NLC OPAC (HTML) ------------------------------------------------------

    let nlcHasMetadataTable (html: string) =
        Regex.IsMatch(html, "<table[^>]+id\\s*=\\s*['\"]td['\"]", RegexOptions.IgnoreCase)

    let nlcFirstDetailUrl (html: string) =
        let matchResult = Regex.Match(html,
            "<div[^>]+class\\s*=\\s*['\"][^'\"]*itemtitle[^'\"]*['\"][^>]*>.*?<a[^>]+href\\s*=\\s*['\"](?<url>[^'\"]+)",
            RegexOptions.IgnoreCase ||| RegexOptions.Singleline)
        if not matchResult.Success then None
        else
            let value = Net.WebUtility.HtmlDecode(matchResult.Groups["url"].Value)
            let mutable absolute = Uri("http://opac.nlc.cn/")
            if Uri.TryCreate(value, UriKind.Absolute, &absolute) then Some (absolute.ToString())
            else Some (Uri(Uri("http://opac.nlc.cn"), value).ToString())

    let private decodeHtml (html: string) =
        match Net.WebUtility.HtmlDecode html with
        | null -> String.Empty
        | text -> text

    let private nlcText (html: string) =
        Regex.Replace(decodeHtml (Regex.Replace(html, "<[^>]+>", String.Empty)), "\\s+", " ").Trim()

    /// Parses the NLC Aleph OPAC detail HTML and requires the requested ISBN to appear.
    let parseNlcHtml (html: string) (requested: NormalizedIdentifier) =
        if not (nlcHasMetadataTable html) then None
        else
            let isbnPattern =
                requested.Value
                |> Seq.map (fun character -> Regex.Escape(string character))
                |> String.concat "(?:\\s|&nbsp;|-)*"
            if not (Regex.IsMatch(html, isbnPattern, RegexOptions.IgnoreCase)) then None
            else
                let fields = Dictionary<string, ResizeArray<string>>(StringComparer.OrdinalIgnoreCase)
                for row in Regex.Matches(html, "<tr[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase ||| RegexOptions.Singleline) do
                    let cells = Regex.Matches(row.Groups["row"].Value,
                        "<td[^>]*class\\s*=\\s*['\"][^'\"]*td1[^'\"]*['\"][^>]*>(?<value>.*?)</td>",
                        RegexOptions.IgnoreCase ||| RegexOptions.Singleline)
                    if cells.Count = 2 then
                        let label = nlcText cells[0].Groups["value"].Value
                        let value = nlcText cells[1].Groups["value"].Value
                        if label.Length > 0 && value.Length > 0 then
                            let mutable values = ResizeArray<string>()
                            if not (fields.TryGetValue(label, &values)) then
                                values <- ResizeArray<string>()
                                fields[label] <- values
                            values.Add(value)
                let firstOf (name: string) =
                    let mutable values = ResizeArray<string>()
                    if fields.TryGetValue(name, &values) then values |> Seq.tryHead else None
                match firstOf "题名与责任" with
                | None -> None
                | Some rawTitle ->
                    let title = Regex.Replace(rawTitle, "\\s*\\[[^\\]]+\\].*$", String.Empty).Trim()
                    if String.IsNullOrWhiteSpace title then None
                    else
                        let publication = Option.defaultValue "" (firstOf "出版项")
                        let yearMatch = Regex.Match(publication, "\\b\\d{4}\\b")
                        let publisherMatch = Regex.Match(publication, ":\\s*(?<publisher>[^,，]+)")
                        let publisher = publisherMatch.Groups["publisher"].Value.Trim()
                        let authors =
                            (Option.defaultValue "" (firstOf "著者"))
                                .Split([| '\n'; ';'; '；' |], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                            |> Array.map (fun value -> Regex.Replace(value, "\\s*(著|编|译)$", String.Empty).Trim())
                            |> Array.filter (fun value -> value.Length > 0)
                            |> Array.map (fun value -> MetadataCreator(authorRole, Literal = value))
                        let tags =
                            Array.append
                                ((Option.defaultValue "" (firstOf "主题"))
                                    .Split([| "--"; ";"; "；" |], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries))
                                ((Option.defaultValue "" (firstOf "中图分类号"))
                                    .Split([| ';'; '；' |], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries))
                            |> Seq.distinctBy (fun value -> value.ToLowerInvariant())
                            |> Seq.toArray
                        Some (MetadataCandidate("",
                            title,
                            Creators = authors,
                            Dates = (if yearMatch.Success && yearMatch.Value.Length = 4 then
                                         [| MetadataDate(issuedRole, [| Int32.Parse(yearMatch.Value, CultureInfo.InvariantCulture) |]) |]
                                     else Array.empty),
                            Publisher = (if publisher.Length = 0 then null else publisher),
                            Abstract = (firstOf "内容提要" |> Option.toObj),
                            Tags = tags,
                            Identifiers = [| MetadataIdentifier("isbn", requested.Value) |],
                            SuggestedItemType = "book",
                            TypeConfidence = 0.95))
