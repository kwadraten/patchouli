#load "Common.fsx"
open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open Common

let verifiedFile directory name (url: string) expectedBytes digest (byteRange: (int64 * int64) option) =
    let destination = Path.Combine(directory, name)
    let verify path = FileInfo(path).Length = expectedBytes && sha256 path = digest
    if File.Exists(destination) then
        if not (verify destination) then failwith $"Existing model file did not match the pinned release: {destination}"
        printfn "Verified %s" name
    else
        let temporary = destination + ".partial-" + Guid.NewGuid().ToString("N")
        try
            use client = new HttpClient(Timeout = TimeSpan.FromHours(2.))
            use request = new HttpRequestMessage(HttpMethod.Get, url)
            match byteRange with Some (first, last) -> request.Headers.Range <- RangeHeaderValue(Nullable first, Nullable last) | None -> ()
            use response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).GetAwaiter().GetResult()
            response.EnsureSuccessStatusCode() |> ignore
            match byteRange with
            | Some (first, last) ->
                let range = response.Content.Headers.ContentRange
                if response.StatusCode <> HttpStatusCode.PartialContent || isNull range || range.From <> Nullable(first) || range.To <> Nullable(last) then
                    failwith $"Model server did not honor the byte range for {name}"
            | None -> ()
            use source = response.Content.ReadAsStreamAsync(cancellation.Token).GetAwaiter().GetResult()
            do
                use target = new FileStream(temporary, FileMode.CreateNew)
                source.CopyToAsync(target, cancellation.Token).GetAwaiter().GetResult()
            if not (verify temporary) then failwith $"Downloaded model file did not match the pinned release: {name}"
            File.Move(temporary, destination, false)
            printfn "Downloaded %s" name
        finally
            if File.Exists(temporary) then File.Delete(temporary)
