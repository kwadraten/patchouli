using System.IO.Compression;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Ocr.MinerU;
using Patchouli.Reading;
using Dapper;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Patchouli.Tests;

public sealed class MinerUDocumentTreeTests
{
    [Fact]
    public async Task Middle_json_imports_typed_content_image_formulas_and_subtypes_without_provider_metadata()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("middle_json.json", """
                                                   {"schema":"docvortex.middle","schema_version":"2.0",
                                                    "metadata":{"file_suffix":"pdf","producer":{"name":"DO_NOT_PERSIST","version":"4.0.12"}},
                                                    "extensions":{"mineru":{"tier":"flash","parse_mode":"txt"}},"is_full_document":false,
                                                    "pages":[{"page_idx":1,"blocks":[
                                                      {"type":"paragraph_title","level":2,"index":0,"bbox":[0.1,0.1,0.9,0.2],
                                                       "content":[{"type":"text","content":"Formula"}]},
                                                      {"type":"code","sub_type":"algorithm","index":1,"bbox":[0.1,0.3,0.9,0.4],"content":[
                                                        {"type":"algorithm_body","content":[{"type":"text","content":"Choose "},{"type":"equation_inline","content":"x_i"}]}]},
                                                      {"type":"equation","index":2,"bbox":[0.279,0.513,0.717,0.549],"content":"","image_path":"images/equation.png"},
                                                      {"type":"list","sub_type":"ref_text","index":3,"bbox":[0.1,0.6,0.9,0.7],"content":[
                                                        {"type":"ref_text","content":[{"type":"text","content":"Reference"}]}]},
                                                      {"type":"aside_text","index":4,"bbox":[0.01,0.1,0.05,0.9],
                                                       "content":[{"type":"text","content":"Margin"}]}
                                                    ]}]}
                                                   """);
        try
        {
            Result<MinerUImportResult> imported = await new MinerUResultImporter(context.Database.ConnectionFactory,
                context.Clock, new OcrDocumentTreeImporter(context.Trees)).ImportResultZipAsync(
                new MinerUImportRequest(zip, context.DocumentId.ToString(), context.LibraryId.ToString()));
            imported.IsSuccess.Should().BeTrue(imported.ErrorMessage);
            DocumentTreeRevisionId revision =
                DocumentTreeRevisionId.Parse(imported.Value.WorkingTreeRevisionIds.Single());
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(revision)).Value;
            boxes.Select(box => box.BoxType).Should().Equal("title", "code", "equation", "list", "aside_text");
            boxes[0].HeadingLevel.Should().Be(2);
            boxes[1].SubType.Should().Be("algorithm");
            boxes[1].Payload.Should().BeOfType<AlgorithmBoxPayload>().Which.Markdown.Should().Be("Choose $x_i$");
            boxes[2].Payload.Should().Be(new EquationBoxPayload("", true));
            boxes[2].BBox.Should().Be(new NormalizedBBox(.279, .513, .438, .036));
            boxes[3].SubType.Should().Be("ref_text");
            boxes[4].Suppressed.Should().BeTrue();
            (await context.Trees.CommitWorkingRevisionAsync(revision)).IsSuccess.Should().BeTrue();

            CompiledMarkdown compiled = (await new DocumentMarkdownCompiler(context.Trees, new MarkdigMarkdownEngine())
                .CompilePageMarkdownAsync(revision)).Value;
            ReadingScene scene = ReadingSceneBuilder.Build(compiled.Document!, compiled.SourceMap, boxes, "page:1",
                markdownSource: compiled.Markdown);
            scene.Blocks.Should().Contain(block => block.Kind == "equation" && block.Image != null);
            string xml = PageXmlCodec.EncodePage(context.DocumentId, 2, revision, boxes);
            xml.Should().Contain("profile=\"patchouli-page-2\"").And.Contain("<image kind=\"page-region\"").And
                .NotContain("base_type").And
                .NotContain("DO_NOT_PERSIST");
            System.Xml.Linq.XElement equation = System.Xml.Linq.XDocument.Parse(xml).Descendants("box")
                .Single(element => (string?)element.Attribute("type") == "equation");
            PageXmlCodec.DecodeBox(equation.ToString(), boxes[2]).Value.Should().Be(new EquationBoxPayload("", true));
            equation.Element("image")!.SetAttributeValue("width", "1");
            PageXmlCodec.DecodeBox(equation.ToString(), boxes[2]).IsFailure.Should().BeTrue();
            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateReadConnection();
            await connection.OpenAsync();
            string payloads = string.Join("",
                await connection.QueryAsync<string>("select payload_json from document_boxes;"));
            payloads.Should().NotContain("DO_NOT_PERSIST").And.NotContain("producer").And.NotContain("mineru");
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Theory]
    [InlineData("9.0", "equation")]
    [InlineData("2.0", "unimplemented_widget")]
    public async Task Middle_json_rejects_unknown_protocol_or_type_without_text_fallback(string version, string type)
    {
        await using Context context = await Context.CreateAsync();
        string json = JsonSerializer.Serialize(new
        {
            schema = "docvortex.middle", schema_version = version,
            pages = new[]
            {
                new
                {
                    page_idx = 0,
                    blocks = new[] { new { type, index = 0, bbox = new[] { .1, .1, .9, .2 }, content = "x" } }
                }
            }
        });
        string zip = CreateZip("middle_json.json", json);
        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(context.Database.ConnectionFactory,
                context.Clock, new OcrDocumentTreeImporter(context.Trees)).ImportResultZipAsync(
                new MinerUImportRequest(zip, context.DocumentId.ToString(), context.LibraryId.ToString()));
            result.IsFailure.Should().BeTrue();
            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateReadConnection();
            await connection.OpenAsync();
            (await connection.ExecuteScalarAsync<int>("select count(*) from document_boxes;")).Should().Be(0);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Middle_json_preserves_visual_subtype_annotations_code_language_and_inline_formula_text()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("middle_json.json", """
                                                   {"schema":"docvortex.middle","schema_version":"2.0","pages":[{"page_idx":0,"blocks":[
                                                     {"type":"code","sub_type":"code","guess_lang":"python","index":0,"bbox":[0.1,0.1,0.9,0.2],"content":[
                                                       {"type":"code_body","content":"if x < 3:\n    print(x)"}]},
                                                     {"type":"image","sub_type":"seal","index":1,"bbox":[0.1,0.3,0.9,0.4],"content":[
                                                       {"type":"image_body","content":"Seal transcription","image_path":"images/seal.png"},
                                                       {"type":"image_caption","content":[{"type":"text","content":"Seal caption"}]}]},
                                                     {"type":"paragraph_title","level":3,"index":2,"bbox":[0.1,0.5,0.9,0.6],"content":[
                                                       {"type":"text","content":"Value "},{"type":"equation_inline","content":"x_i"},
                                                       {"type":"text","content":" <option>"}]}
                                                   ]}]}
                                                   """);
        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(context.Database.ConnectionFactory,
                context.Clock, new OcrDocumentTreeImporter(context.Trees)).ImportResultZipAsync(
                new MinerUImportRequest(zip, context.DocumentId.ToString(), context.LibraryId.ToString()));
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            DocumentTreeRevisionId revision =
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single());
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(revision)).Value;
            boxes[0].CodeLanguage.Should().Be("python");
            boxes[0].SubType.Should().Be("code");
            boxes[0].Payload.Should().BeOfType<CodeBoxPayload>().Which.Code.Should().Be("if x < 3:\n    print(x)");
            boxes[1].SubType.Should().Be("seal");
            DocumentBoxProjection.PlainText(boxes[1], new MarkdigMarkdownEngine()).Should().Contain("Seal caption");
            boxes[1].Payload!.Annotations.Should().Equal(new DocumentAnnotation("image_caption", "Seal caption"));
            boxes[2].Payload.Should().BeOfType<TextBoxPayload>().Which.Markdown.Should().Contain("$x_i$");
            CompiledMarkdown compiled = (await new DocumentMarkdownCompiler(context.Trees, new MarkdigMarkdownEngine())
                .CompilePageMarkdownAsync(revision)).Value;
            compiled.Markdown.Split("Seal caption", StringSplitOptions.None).Should().HaveCount(2);
            ReadingScene scene = ReadingSceneBuilder.Build(compiled.Document!, compiled.SourceMap, boxes, "page",
                markdownSource: compiled.Markdown);
            scene.Blocks.Count(block => block.Kind == "media").Should().Be(1);
            scene.Blocks.Should().Contain(block => block.Kind == "paragraph" && block.Text == "Seal caption");
            string xml = PageXmlCodec.EncodePage(context.DocumentId, 1, revision, boxes);
            System.Xml.Linq.XElement image = System.Xml.Linq.XDocument.Parse(xml).Descendants("box")
                .Single(element => (string?)element.Attribute("type") == "image");
            PageXmlCodec.DecodeBox(image.ToString(), boxes[1]).IsSuccess.Should().BeTrue();
            image.Element("annotation")!.Value = "changed";
            PageXmlCodec.DecodeBox(image.ToString(), boxes[1]).IsFailure.Should().BeTrue();
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Migration_removes_legacy_base_type_and_preserves_history_ids_links_and_payload_references()
    {
        string legacyDirectory = Path.Combine(Path.GetTempPath(), "legacy-box-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(legacyDirectory);
        try
        {
            foreach (string file in Directory.EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql"))
            {
                if (Path.GetFileName(file).StartsWith("048_", StringComparison.Ordinal))
                {
                    continue;
                }

                string sql = await File.ReadAllTextAsync(file);
                if (Path.GetFileName(file).StartsWith("005_", StringComparison.Ordinal))
                {
                    sql = sql.Replace("sub_type text null,", "sub_type text null,\n    base_type text null,",
                            StringComparison.Ordinal)
                        .Replace("check (length(trim(box_type)) > 0),",
                            "check (length(trim(box_type)) > 0),\n    check (base_type is null or base_type in ('text','image','table','code','unknown')),",
                            StringComparison.Ordinal);
                }

                await File.WriteAllTextAsync(Path.Combine(legacyDirectory, Path.GetFileName(file)), sql);
            }

            await using Context context = await Context.CreateAsync(legacyDirectory);
            PageId pageId;
            await using (SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection())
            {
                await connection.OpenAsync();
                pageId = PageId.Parse(
                    await connection.ExecuteScalarAsync<string>(
                        "select page_id from pages order by page_index limit 1;")
                    ?? throw new InvalidOperationException("Test page is missing."));
            }

            DocumentTreeRevision revision = await BoxTreeTestData.CommitTextAsync(context.Database.ConnectionFactory,
                context.Clock, context.DocumentId, pageId, "");
            DocumentBox before = (await context.Trees.ListBoxesAsync(revision.TreeRevisionId)).Value.Single();
            await using (SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection())
            {
                await connection.OpenAsync();
                // Reproduce the legacy importer writing a working row, then adopting it.
                await connection.ExecuteAsync("update document_tree_revisions set status='working',is_current=0;");
                await connection.ExecuteAsync(
                    "update document_boxes set box_type='equation_interline', base_type='text';");
                await connection.ExecuteAsync("update document_tree_revisions set status='committed',is_current=1;");
            }

            await new MigrationRunner(context.Database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            DocumentBox after = (await context.Trees.ListBoxesAsync(revision.TreeRevisionId)).Value.Single();
            after.BoxId.Should().Be(before.BoxId);
            after.TreeRevisionId.Should().Be(before.TreeRevisionId);
            after.BBox.Should().Be(before.BBox);
            after.Payload.Should().Be(new EquationBoxPayload("", true));
            await using (SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection())
            {
                await connection.OpenAsync();
                (await connection.QueryAsync<string>("select name from pragma_table_info('document_boxes');"))
                    .Should().NotContain("base_type");
                (await connection.QueryAsync<dynamic>("pragma foreign_key_check;")).Should().BeEmpty();
                // Recreated payload-reference triggers must remain functional after the table rebuild.
                SqliteException? rejection = null;
                try
                {
                    await connection.ExecuteAsync("update document_boxes set suppressed=1;");
                }
                catch (SqliteException exception)
                {
                    rejection = exception;
                }

                rejection.Should().NotBeNull();
                rejection!.Message.Should().Contain("immutable");
                await connection.ExecuteAsync("update document_tree_revisions set status='working',is_current=0;");
                string asset = Guid.NewGuid().ToString();
                await connection.ExecuteAsync("update document_boxes set box_type='image', payload_json=@Payload;",
                    new { Payload = JsonSerializer.Serialize(new { assetId = asset, description = "Figure" }) });
                (await connection.ExecuteScalarAsync<string>("select file_asset_id from file_asset_payload_refs;"))
                    .Should().Be(asset);
            }

            (await new MigrationRunner(context.Database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync())
                .Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(legacyDirectory, true);
        }
    }

    [Fact]
    public async Task V2_interline_equation_persists_latex_and_projects_the_page_crop()
    {
        await using Context context = await Context.CreateAsync();
        const string latex =
            @"\mathrm{BI}=1-|r_{pb}(p,y)|,\quad r_{pb}(p,y)=\frac{\mu_{p,y=1}-\mu_{p,y=0}}{\sigma_p}\sqrt{q(1-q)}";
        string zip = CreateZip("_content_list_v2.json", """
                                                        [[{"type":"equation_interline","content":{
                                                          "math_content":"\\mathrm{BI}=1-|r_{pb}(p,y)|,\\quad r_{pb}(p,y)=\\frac{\\mu_{p,y=1}-\\mu_{p,y=0}}{\\sigma_p}\\sqrt{q(1-q)}",
                                                          "math_type":"latex","image_source":{"path":"images/formula.jpg"}},
                                                          "bbox":[279,513,717,549]}]]
                                                        """);
        try
        {
            Result<MinerUImportResult> imported = await new MinerUResultImporter(context.Database.ConnectionFactory,
                    context.Clock, new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(zip, context.DocumentId.ToString(),
                    context.LibraryId.ToString()));
            imported.IsSuccess.Should().BeTrue(imported.ErrorMessage);
            DocumentTreeRevisionId revision =
                DocumentTreeRevisionId.Parse(imported.Value.WorkingTreeRevisionIds.Single());
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(revision)).Value;
            DocumentBox equation = boxes.Should().ContainSingle().Subject;
            equation.BoxType.Should().Be(DocumentBoxType.Equation);
            equation.Payload.Should().Be(new EquationBoxPayload(latex, true));
            (await context.Trees.CommitWorkingRevisionAsync(revision)).IsSuccess.Should().BeTrue();

            await using SqliteConnection connection = context.Database.ConnectionFactory.CreateReadConnection();
            await connection.OpenAsync();
            string json = await connection.QuerySingleAsync<string>(
                "select payload_json from document_boxes where tree_revision_id=@Revision;",
                new { Revision = revision.ToString() });
            using JsonDocument stored = JsonDocument.Parse(json);
            stored.RootElement.GetProperty("latex").GetString().Should().Be(latex);
            stored.RootElement.TryGetProperty("markdown", out _).Should().BeFalse();

            CompiledMarkdown compiled = (await new DocumentMarkdownCompiler(context.Trees, new MarkdigMarkdownEngine())
                .CompilePageMarkdownAsync(revision)).Value;
            foreach (string key in new[] { "current-page", "page:2" })
            {
                ReadingBlock block = ReadingSceneBuilder.Build(compiled.Document!, compiled.SourceMap, boxes, key,
                    markdownSource: compiled.Markdown).Blocks.Should().ContainSingle().Subject;
                block.Kind.Should().Be("equation");
                block.Image.Should().Be(new ReadingImageRegion(key, new NormalizedBBox(.279, .513, .438, .036)));
            }
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Importer_escapes_paper_prompt_placeholders_and_preserves_their_display_text()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list_v2.json", """
                                                        [[
                                                          {"type":"paragraph","content":{"paragraph_content":[{"type":"text","content":"What did the AI assistant say about the \"<feature>\" for Option \"<option>\"?"}]},"bbox":[0,0,1000,200]},
                                                          {"type":"list","content":{"list_type":"reference_list","list_items":[{"item_type":"text","item_content":[{"type":"text","content":"Choose <option>"}]}]},"bbox":[0,200,1000,400]}
                                                        ]]
                                                        """);
        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock, new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(zip, context.DocumentId.ToString(),
                    context.LibraryId.ToString()));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            DocumentTreeRevisionId revisionId =
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single());
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(revisionId)).Value;
            boxes[0].Payload.Should().Be(new TextBoxPayload(
                "What did the AI assistant say about the \"<feature>\" for Option \"&lt;option>\"?"));
            boxes[1].Payload.Should().Be(new ListBoxPayload("- Choose &lt;option>"));
            MarkdigMarkdownEngine markdown = new();
            CompiledMarkdown compiled = (await new DocumentMarkdownCompiler(context.Trees, markdown)
                .CompilePageMarkdownAsync(revisionId)).Value;
            compiled.Markdown.Should().Contain("&lt;option>");
            string.Concat(compiled.Document!.Blocks.First().Inlines!.Select(inline => inline.Text))
                .Should().Be("What did the AI assistant say about the \"<feature>\" for Option \"<option>\"?");
            (await context.Trees.CommitWorkingRevisionAsync(revisionId)).IsSuccess.Should().BeTrue();
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Importer_preserves_v2_page_footer_content_as_suppressed_text()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list_v2.json", """
                                                        [[
                                                          {"type":"page_footer","content":{"page_footer_content":[{"type":"text","content":"JSTOR"}]},"bbox":[0,900,1000,950]}
                                                        ]]
                                                        """);

        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock,
                    new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(
                    zip, context.DocumentId.ToString(), context.LibraryId.ToString()));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single()))).Value;
            boxes.Should().ContainSingle().Which.Should().BeEquivalentTo(new
            {
                BoxType = DocumentBoxType.Footer,
                Suppressed = true,
                Payload = new TextBoxPayload("JSTOR")
            });
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Importer_creates_a_logical_page_placeholder_for_an_empty_mineru_page()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list_v2.json", """
                                                        [
                                                          [
                                                            {"type":"paragraph","content":{"paragraph_content":[{"type":"text","content":"First page"}]},"bbox":[0,0,1000,1000]}
                                                          ],
                                                          []
                                                        ]
                                                        """);

        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock,
                    new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(
                    zip, context.DocumentId.ToString(), context.LibraryId.ToString()));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.BoxesCreated.Should().Be(2);
            result.Value.Warnings.Should().Contain("blank_page_placeholder");
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds[1]))).Value;
            boxes.Should().ContainSingle().Which.Should().BeEquivalentTo(new
            {
                BoxType = DocumentBoxType.LogicalPage,
                Payload = new TextBoxPayload("Blank page (MinerU returned no content).")
            });
            (await context.Trees.CommitWorkingRevisionAsync(
                    DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds[1])))
                .IsSuccess.Should().BeTrue();
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Importer_preserves_v2_auxiliary_content_as_suppressed_text()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list_v2.json", """
                                                        [[
                                                          {"type":"page_header","content":{"page_header_content":[{"type":"text","content":"Running head"}]},"bbox":[0,0,1000,50]},
                                                          {"type":"page_number","content":{"page_number_content":[{"type":"text","content":"7"}]},"bbox":[900,950,1000,1000]},
                                                          {"type":"page_footnote","content":{"page_footnote_content":[{"type":"text","content":"Footnote"}]},"bbox":[0,850,1000,900]}
                                                        ]]
                                                        """);

        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock,
                    new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(
                    zip, context.DocumentId.ToString(), context.LibraryId.ToString()));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single()))).Value;
            boxes.Select(box => new
                {
                    box.BoxType,
                    box.Suppressed,
                    Markdown = ((TextBoxPayload)box.Payload!).Markdown
                })
                .Should().BeEquivalentTo([
                    new { BoxType = DocumentBoxType.Header, Suppressed = true, Markdown = "Running head" },
                    new { BoxType = DocumentBoxType.PageNumber, Suppressed = true, Markdown = "7" },
                    new { BoxType = DocumentBoxType.PageFootnote, Suppressed = true, Markdown = "Footnote" }
                ]);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Importer_stages_typed_boxes_suppresses_auxiliary_and_builds_gfm_table()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list_v2.json", """
                                                        [
                                                          {"type":"title","level":2,"page_idx":0,"text":"Heading","bbox":[0,0,500,100]},
                                                          {"type":"header","page_idx":0,"text":"Running head","bbox":[0,110,500,150]},
                                                          {"type":"table","page_idx":0,"bbox":[0,200,800,500],"cells":[
                                                            {"row_index":0,"col_index":0,"text":"Name"},
                                                            {"row_index":0,"col_index":1,"text":"Value"},
                                                            {"row_index":1,"col_index":0,"text":"Pages"},
                                                            {"row_index":1,"col_index":1,"text":"12"}
                                                          ]}
                                                        ]
                                                        """);

        try
        {
            MinerUResultImporter importer = new(context.Database.ConnectionFactory, context.Clock,
                new OcrDocumentTreeImporter(context.Trees));
            Result<MinerUImportResult> result = await importer.ImportResultZipAsync(
                new MinerUImportRequest(zip, context.DocumentId.ToString(), context.LibraryId.ToString()));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.BoxesCreated.Should().Be(3);
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single()))).Value;
            boxes.Should().Contain(box => box.BoxType == DocumentBoxType.Title && box.HeadingLevel == 2);
            boxes.Should().Contain(box => box.BoxType == DocumentBoxType.Header && box.Suppressed);
            boxes.Single(box => box.BoxType == DocumentBoxType.Table).Payload.Should().BeOfType<TableBoxPayload>()
                .Which.Markdown.Should().Contain("| Name | Value |").And.Contain("| Pages | 12 |");
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Imported_authoritative_html_table_can_be_edited_committed_and_reopened_with_identity_and_spans()
    {
        await using Context context = await Context.CreateAsync();
        const string html = "<table><thead><tr><th rowspan=\"2\">Metric</th><th colspan=\"2\">Period</th></tr>" +
                            "<tr><th>2024</th><th>2025</th></tr></thead><tbody><tr><td><strong>Revenue</strong></td>" +
                            "<td><eq>LaTex</eq></td><td><em>10</em></td></tr></tbody></table>";
        const string jsonTemplate = """
                                    {"pages":[{"page_num":1,"width":800,"height":1000,"blocks":[{"type":"table","bbox":[0,0,800,500],"content":{"html":"__HTML__"}}]}]}
                                    """;
        string json = jsonTemplate.Replace("\"__HTML__\"", JsonSerializer.Serialize(html), StringComparison.Ordinal);
        string zip = CreateZip("_content_list_v2.json", json);
        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock,
                    new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(
                    zip, context.DocumentId.ToString(), context.LibraryId.ToString()));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single()))).Value;
            TableBoxPayload table = boxes.Should().ContainSingle().Which.Payload.Should()
                .BeOfType<TableBoxPayload>().Subject;
            table.ContentFormat.Should().Be(TableContentFormat.Html);
            table.ContentFormatVersion.Should().Be(TableContentFormat.AuthoritativeHtmlVersion);
            table.Markdown.Should().BeEmpty();
            table.Html.Should().Be(html);

            DocumentTreeRevisionId workingRevisionId =
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single());
            IReadOnlyList<DocumentBox> workingBoxes = (await context.Trees.ListBoxesAsync(workingRevisionId)).Value;
            DocumentBox importedBox = workingBoxes.Should().ContainSingle().Which;
            PageId pageId;
            await using (SqliteConnection connection = context.Database.ConnectionFactory.CreateReadConnection())
            {
                await connection.OpenAsync();
                pageId = PageId.Parse(await connection.QuerySingleAsync<string>(
                    "select page_id from document_tree_revisions where tree_revision_id = @RevisionId;",
                    new { RevisionId = workingRevisionId.ToString() }));
            }

            DocumentTreeRevision initiallyCommitted =
                (await context.Trees.CommitWorkingRevisionAsync(workingRevisionId)).Value;
            PageEditSession edit = (await context.Trees.BeginPageEditAsync(context.DocumentId, pageId)).Value;
            const string editedHtml =
                "<table><thead><tr><th rowspan=\"2\">Metric</th><th colspan=\"2\">Period</th></tr>" +
                "<tr><th>2024</th><th>2025</th></tr></thead><tbody><tr><td><strong>Sales</strong></td>" +
                "<td><eq>LaTex</eq></td><td><em>10</em></td></tr></tbody></table>";
            TableBoxPayload editedPayload = TableContentAdapter.CreateHtml(editedHtml).Value;
            (await context.Trees.UpdateLeafAsync(edit.SessionId,
                    new UpdateLeafCommand(importedBox.BoxId, DocumentBoxType.Table, editedPayload))).IsSuccess.Should()
                .BeTrue();
            DocumentTreeRevision committed = (await context.Trees.CommitPageEditAsync(edit.SessionId)).Value;

            IReadOnlyList<DocumentBox> reopenedBoxes =
                (await context.Trees.ListBoxesAsync(committed.TreeRevisionId)).Value;
            DocumentBox reopenedBox = reopenedBoxes.Should().ContainSingle().Which;
            reopenedBox.BoxId.Should().Be(importedBox.BoxId);
            reopenedBox.BBox.Should().Be(importedBox.BBox);
            reopenedBox.Payload.Should().Be(editedPayload);
            committed.ParentTreeRevisionId.Should().Be(initiallyCommitted.TreeRevisionId);

            CompiledMarkdown compiled = (await new DocumentMarkdownCompiler(context.Trees, new MarkdigMarkdownEngine())
                .CompilePageMarkdownAsync(committed.TreeRevisionId, includeComplexTableHtml: true)).Value;
            ReadingScene scene = ReadingSceneBuilder.Build(compiled.Document!, compiled.SourceMap, reopenedBoxes,
                "edited-page", markdownSource: compiled.Markdown);
            ReadingBlock readingTable = scene.Blocks.Should().ContainSingle(block => block.Kind == "table").Which;
            readingTable.BoxId.Should().Be(importedBox.BoxId);
            ReadingTableCell[] cells = readingTable.Table!.Cells!.ToArray();
            cells.Should().Contain(cell => cell.Text == "Sales" && cell.Row == 2);
            cells.Should().Contain(cell => cell.Text == "Metric" && cell.RowSpan == 2);
            cells.Should().Contain(cell => cell.Text == "Period" && cell.ColumnSpan == 2);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Importer_rejects_full_markdown_without_tree_artifact()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("full.md", "# Not a tree");
        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock,
                    new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(
                    zip, context.DocumentId.ToString(), context.LibraryId.ToString()));
            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be("tree_artifact_required");
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Valid_single_row_spanned_html_table_stays_authoritative_without_gfm_diagnostic()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list_v2.json", """
                                                        {"pages":[{"page_num":1,"width":800,"height":1000,"blocks":[{"type":"table","bbox":[0,0,800,500],"content":{"html":"<table><tr><td rowspan=\"2\">Merged</td></tr></table>"}}]}]}
                                                        """);
        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock,
                    new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(
                    zip, context.DocumentId.ToString(), context.LibraryId.ToString()));
            result.Value.Warnings.Should().NotContain("table_not_representable_as_gfm");
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single()))).Value;
            boxes.Single().Payload.Should().Be(TableContentAdapter.CreateHtml(
                "<table><tr><td rowspan=\"2\">Merged</td></tr></table>").Value);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Importer_stages_each_physical_page_from_a_multi_page_content_list()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list.json", """
                                                     [
                                                       {"type":"text","page_idx":0,"text":"First physical page","bbox":[0,0,800,100]},
                                                       {"type":"text","page_idx":1,"text":"Second physical page","bbox":[0,0,800,100]}
                                                     ]
                                                     """);
        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock,
                    new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(
                    zip, context.DocumentId.ToString(), context.LibraryId.ToString()));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.WorkingTreeRevisionIds.Should().HaveCount(2);
            (await context.Trees.ListBoxesAsync(DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds[0])))
                .Value.Single().Payload.Should().Be(new TextBoxPayload("First physical page"));
            (await context.Trees.ListBoxesAsync(DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds[1])))
                .Value.Single().Payload.Should().Be(new TextBoxPayload("Second physical page"));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Importer_preserves_real_mineru_text_shapes_as_single_box_payloads()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list_v2.json", """
                                                        [[
                                                          {"type":"paragraph","content":{"paragraph_content":[{"type":"text","content":"First OCR paragraph.\n\nSecond OCR paragraph with a forced\nline break."}]},"bbox":[0,0,800,300]},
                                                          {"type":"page_footnote","content":{"page_footnote_content":[{"type":"text","content":"1. William S. Lewis and Naojiro Murakami, eds."}]},"bbox":[0,700,800,800]},
                                                          {"type":"paragraph","content":{"paragraph_content":[{"type":"text","content":"天保三年遭難\n寶順九衆組員之墓\n乙舌、久舌、岩松等十四人"}]},"bbox":[0,400,800,650]}
                                                        ]]
                                                        """);

        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock,
                    new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(
                    zip, context.DocumentId.ToString(), context.LibraryId.ToString()));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single()))).Value;
            boxes.Select(box => ((TextBoxPayload)box.Payload!).Markdown).Should().BeEquivalentTo([
                "First OCR paragraph.\n\nSecond OCR paragraph with a forced\nline break.",
                "1. William S. Lewis and Naojiro Murakami, eds.",
                "天保三年遭難\n寶順九衆組員之墓\n乙舌、久舌、岩松等十四人"
            ]);
            MarkdigMarkdownEngine markdown = new();
            boxes.Should().OnlyContain(box => markdown.ValidateLeaf(box.BoxType, box.Payload!).IsSuccess);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Importer_merges_text_fully_contained_by_an_image_into_its_description()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list_v2.json", """
                                                        [[
                                                          {"type":"paragraph","content":{"paragraph_content":[{"type":"text","content":"天保三年遭難\n寶順九衆組員之墓\n乙舌、久舌、岩松等十四人"}]},"bbox":[501,106,547,210]},
                                                          {"type":"image","content":{"image_source":{"path":"images/stone.jpg"},"content":"","image_caption":[],"image_footnote":[]},"bbox":[379,106,641,510]}
                                                        ]]
                                                        """);

        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock,
                    new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(
                    zip, context.DocumentId.ToString(), context.LibraryId.ToString()));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.BoxesCreated.Should().Be(1);
            result.Value.Warnings.Should().Contain("image_embedded_text_merged");
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single()))).Value;
            DocumentBox image = boxes.Should().ContainSingle().Which;
            image.BoxType.Should().Be(DocumentBoxType.Image);
            image.Payload.Should().Be(new MediaBoxPayload(
                null,
                "天保三年遭難\n寶順九衆組員之墓\n乙舌、久舌、岩松等十四人"));
            (await context.Trees.CommitWorkingRevisionAsync(
                    DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single())))
                .IsSuccess.Should().BeTrue();
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Importer_links_an_empty_paragraph_region_to_the_box_holding_its_text()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list_v2.json", """
                                                        [
                                                          [
                                                            {"type":"paragraph","content":{"paragraph_content":[{"type":"text","content":"Full paragraph text kept in the first box."}]},"bbox":[100,600,900,800]},
                                                            {"type":"page_footer","content":{"page_footer_content":[{"type":"text","content":"footer"}]},"bbox":[400,900,600,950]}
                                                          ],
                                                          [
                                                            {"type":"paragraph","content":{"paragraph_content":[]},"bbox":[100,100,900,300]},
                                                            {"type":"paragraph","content":{"paragraph_content":[{"type":"text","content":"A new paragraph."}]},"bbox":[100,400,900,600]}
                                                          ]
                                                        ]
                                                        """);

        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock,
                    new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(
                    zip, context.DocumentId.ToString(), context.LibraryId.ToString()));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Warnings.Should().Contain("paragraph_continuation_linked");
            DocumentBox head = (await context.Trees.ListBoxesAsync(
                    DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds[0]))).Value
                .Single(box => box.BoxType == DocumentBoxType.Text);
            IReadOnlyList<DocumentBox> pageTwo = (await context.Trees.ListBoxesAsync(
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds[1]))).Value;
            DocumentBox continuation = pageTwo.Single(box =>
                box.Payload is TextBoxPayload text && string.IsNullOrWhiteSpace(text.Markdown));
            continuation.ContinuesFromBoxId.Should().Be(head.BoxId);

            foreach (string revisionId in result.Value.WorkingTreeRevisionIds)
            {
                (await context.Trees.CommitWorkingRevisionAsync(DocumentTreeRevisionId.Parse(revisionId)))
                    .IsSuccess.Should().BeTrue();
            }

            DocumentBox committedContinuation = (await context.Trees.ListBoxesAsync(
                continuation.TreeRevisionId)).Value.Single(box => box.BoxId == continuation.BoxId);
            committedContinuation.ContinuesFromBoxId.Should().Be(head.BoxId);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Importer_links_in_page_and_chained_continuation_regions_to_the_same_head()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list_v2.json", """
                                                        [
                                                          [
                                                            {"type":"paragraph","content":{"paragraph_content":[{"type":"text","content":"Column bottom text."}]},"bbox":[100,800,400,900]},
                                                            {"type":"paragraph","content":{"paragraph_content":[]},"bbox":[600,100,900,250]},
                                                            {"type":"paragraph","content":{"paragraph_content":[]},"bbox":[600,300,900,380]},
                                                            {"type":"paragraph","content":{"paragraph_content":[{"type":"text","content":"Next paragraph."}]},"bbox":[600,450,900,600]}
                                                          ]
                                                        ]
                                                        """);

        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock,
                    new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(
                    zip, context.DocumentId.ToString(), context.LibraryId.ToString()));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single()))).Value;
            DocumentBox head = boxes.Single(box =>
                box.Payload is TextBoxPayload text && text.Markdown == "Column bottom text.");
            DocumentBox[] continuations = boxes
                .Where(box => box.Payload is TextBoxPayload text && string.IsNullOrWhiteSpace(text.Markdown))
                .ToArray();
            continuations.Should().HaveCount(2);
            continuations.Should().OnlyContain(box => box.ContinuesFromBoxId == head.BoxId);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Importer_leaves_a_headless_empty_paragraph_unlinked()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list_v2.json", """
                                                        [
                                                          [
                                                            {"type":"paragraph","content":{"paragraph_content":[]},"bbox":[100,100,900,300]},
                                                            {"type":"paragraph","content":{"paragraph_content":[{"type":"text","content":"Some text."}]},"bbox":[100,400,900,600]}
                                                          ]
                                                        ]
                                                        """);

        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock,
                    new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(
                    zip, context.DocumentId.ToString(), context.LibraryId.ToString()));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Warnings.Should().NotContain("paragraph_continuation_linked");
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single()))).Value;
            boxes.Should().OnlyContain(box => box.ContinuesFromBoxId == null);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Importer_maps_v2_list_items_to_gfm_list_payload()
    {
        await using Context context = await Context.CreateAsync();
        string zip = CreateZip("_content_list_v2.json", """
                                                        [[
                                                          {"type":"list","content":{"list_type":"reference_list","list_items":[{"item_type":"text","item_content":[{"type":"text","content":"ff. 202v-203r 1615.2.2\n→[東京大学史料編纂所 1996]"}]}]},"bbox":[0,0,1000,200]},
                                                          {"type":"paragraph","content":{"paragraph_content":[{"type":"text","content":"Title uses <Carta de confirmação> brackets."}]},"bbox":[0,300,1000,500]}
                                                        ]]
                                                        """);
        try
        {
            Result<MinerUImportResult> result = await new MinerUResultImporter(
                    context.Database.ConnectionFactory, context.Clock,
                    new OcrDocumentTreeImporter(context.Trees))
                .ImportResultZipAsync(new MinerUImportRequest(
                    zip, context.DocumentId.ToString(), context.LibraryId.ToString()));

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(
                DocumentTreeRevisionId.Parse(result.Value.WorkingTreeRevisionIds.Single()))).Value;
            boxes.Should().HaveCount(2);
            boxes[0].BoxType.Should().Be(DocumentBoxType.List);
            boxes[0].Payload.Should().Be(new ListBoxPayload(
                "- ff. 202v-203r 1615.2.2\n  →[東京大学史料編纂所 1996]"));
            boxes[1].Payload.Should().Be(new TextBoxPayload(
                "Title uses <Carta de confirmação> brackets."));
            MarkdigMarkdownEngine markdown = new();
            boxes.Should().OnlyContain(box => markdown.ValidateLeaf(box.BoxType, box.Payload!).IsSuccess);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    private static string CreateZip(string entryName, string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mineru-box-{Guid.NewGuid():N}.zip");
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using StreamWriter writer = new(archive.CreateEntry(entryName).Open());
        writer.Write(content);
        return path;
    }

    private sealed class Context : IAsyncDisposable
    {
        private Context(TemporarySqliteDatabase database, FixedClock clock, LibraryId libraryId,
            DocumentInstanceId documentId, DocumentTreeService trees)
        {
            Database = database;
            Clock = clock;
            LibraryId = libraryId;
            DocumentId = documentId;
            Trees = trees;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public LibraryId LibraryId { get; }
        public DocumentInstanceId DocumentId { get; }
        public DocumentTreeService Trees { get; }

        public static async Task<Context> CreateAsync(string? migrationsDirectory = null)
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, migrationsDirectory ?? TestPaths.MigrationsDirectory)
                .RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            LibraryMetadata library = (await libraries.CreateLibraryAsync("MinerU Box Tree")).Value;
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "MinerU")).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            Infrastructure.Layout.PageService pages = new(database.ConnectionFactory, clock);
            await pages.CreatePageAsync(
                document.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage,
                null, null, "test", null);
            await pages.CreatePageAsync(
                document.DocumentInstanceId, 1, "2", null, null, 0, CoordinateBasis.NormalizedPage,
                null, null, "test", null);
            DocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            return new Context(database, clock, library.LibraryId, document.DocumentInstanceId, trees);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
