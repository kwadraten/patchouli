namespace Patchouli.Extensions.Tests

open System
open Patchouli.Core.Ids
open Patchouli.Extensions.Contracts
open BuiltInTranslation.Support
open Xunit

[<Collection(TestEnvironment.CollectionName)>]
type TranslationSupportTests() =
    let box text = ExtensionBox(DocumentBoxId(Guid.NewGuid()), Nullable<DocumentBoxId>(), "text", null, text, "{}")

    [<Fact>]
    member _.``production segmentation respects the character limit and preserves source order`` () =
        let source = box "abcdefghij"
        let segments = segment 4 source
        Assert.Equal<string array>([| "abcd"; "efgh"; "ij" |], segments |> Array.map (fun item -> item.Text))
        Assert.Equal<int array>([| 1; 2; 3 |], segments |> Array.map (fun item -> item.PartIndex))

    [<Fact>]
    member _.``production protection and restoration round trip markup and placeholders`` () =
        let original = "Translate <em>this</em> {{name}} and %s"
        let protectedText, map = protect "test-identity" original
        Assert.NotEqual<string>(original, protectedText)
        Assert.Equal(original, restore protectedText map)

    [<Fact>]
    member _.``production batch builder honors item and character budgets`` () =
        let source = box "abcdefghij"
        let segments = segment 4 source
        let batches = buildSegmentBatches segments 2 8 (Nullable())
        Assert.Equal(2, batches.Length)
        Assert.Equal<int array>([| 2; 1 |], batches |> Array.map Array.length)
        Assert.All(batches, fun batch -> Assert.True(batch |> Array.sumBy (fun item -> item.Text.Length) <= 8))

    [<Fact>]
    member _.``production merge joins successful segments and fails when a segment is missing`` () =
        let source = box "abcdefgh"
        let segments = segment 4 source
        let complete = segments |> Array.map (fun item -> translated item.Id item.Text)
        let merged = mergeSegments [| source |] segments complete
        Assert.Equal(ExtensionBoxOutcomeKind.Translated, merged[0].Kind)
        Assert.Equal("abcdefgh", merged[0].GetTranslatedText())
        let incomplete = mergeSegments [| source |] segments complete[0..0]
        Assert.Equal(ExtensionBoxOutcomeKind.Failed, incomplete[0].Kind)
