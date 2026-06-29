using Dapper;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Layout;
using LiteratureApp.Core.Results;
using LiteratureApp.Core.Time;
using LiteratureApp.Infrastructure.Database;

namespace LiteratureApp.Infrastructure.Layout;

public sealed class PageService : IPageService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public PageService(SqliteConnectionFactory connectionFactory, IClock clock)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public async Task<Result<Page>> CreatePageAsync(
        DocumentInstanceId documentInstanceId,
        int pageIndex,
        string? pageLabel,
        double? width,
        double? height,
        int rotation,
        string coordinateBasis,
        double? basisWidth,
        double? basisHeight,
        string rendererBasisVersion,
        string? sourceFileHash,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidatePageInput(pageIndex, rotation, coordinateBasis, rendererBasisVersion);
        if (validation.IsFailure)
        {
            return Result<Page>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var documentExists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_instances where document_instance_id = @DocumentInstanceId;",
                new { DocumentInstanceId = documentInstanceId.ToString() },
                transaction);

            if (documentExists == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<Page>.Failure(AppErrorCodes.NotFound, "Document instance was not found.");
            }

            var duplicate = await connection.ExecuteScalarAsync<int>(
                """
                select count(1)
                from pages
                where document_instance_id = @DocumentInstanceId and page_index = @PageIndex;
                """,
                new { DocumentInstanceId = documentInstanceId.ToString(), PageIndex = pageIndex },
                transaction);

            if (duplicate > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<Page>.Failure(
                    AppErrorCodes.InvalidState,
                    "A page with this page_index already exists for the document instance.");
            }

            var now = _clock.UtcNow.ToUniversalTime();
            var page = new Page(
                PageId.New(),
                documentInstanceId,
                pageIndex,
                NullIfWhiteSpace(pageLabel),
                width,
                height,
                rotation,
                coordinateBasis.Trim(),
                basisWidth,
                basisHeight,
                rendererBasisVersion.Trim(),
                NullIfWhiteSpace(sourceFileHash),
                now,
                now);

            await connection.ExecuteAsync(
                """
                insert into pages (
                    page_id, document_instance_id, page_index, page_label, width, height,
                    rotation, coordinate_basis, basis_width, basis_height, renderer_basis_version,
                    source_file_hash, created_at, updated_at
                )
                values (
                    @PageId, @DocumentInstanceId, @PageIndex, @PageLabel, @Width, @Height,
                    @Rotation, @CoordinateBasis, @BasisWidth, @BasisHeight, @RendererBasisVersion,
                    @SourceFileHash, @CreatedAt, @UpdatedAt
                );
                """,
                ToParameters(page),
                transaction);

            await transaction.CommitAsync(cancellationToken);
            return Result<Page>.Success(page);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DatabaseFailure<Page>(exception);
        }
    }

    public async Task<Result<IReadOnlyList<Page>>> ListPagesAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var documentExists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_instances where document_instance_id = @DocumentInstanceId;",
                new { DocumentInstanceId = documentInstanceId.ToString() });

            if (documentExists == 0)
            {
                return Result<IReadOnlyList<Page>>.Failure(AppErrorCodes.NotFound, "Document instance was not found.");
            }

            var rows = await connection.QueryAsync<PageRow>(
                SelectPagesSql + " where document_instance_id = @DocumentInstanceId order by page_index;",
                new { DocumentInstanceId = documentInstanceId.ToString() });

            return Result<IReadOnlyList<Page>>.Success(rows.Select(row => row.ToPage()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DatabaseFailure<IReadOnlyList<Page>>(exception);
        }
    }

    public async Task<Result<Page>> GetPageAsync(PageId pageId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var row = await connection.QuerySingleOrDefaultAsync<PageRow>(
                SelectPagesSql + " where page_id = @PageId;",
                new { PageId = pageId.ToString() });

            return row is null
                ? Result<Page>.Failure(AppErrorCodes.NotFound, "Page was not found.")
                : Result<Page>.Success(row.ToPage());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DatabaseFailure<Page>(exception);
        }
    }

    private static Result ValidatePageInput(
        int pageIndex,
        int rotation,
        string coordinateBasis,
        string rendererBasisVersion)
    {
        if (pageIndex < 0)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Page index must be zero or greater.");
        }

        if (rotation is not (0 or 90 or 180 or 270))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Page rotation must be 0, 90, 180, or 270.");
        }

        if (string.IsNullOrWhiteSpace(coordinateBasis))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Coordinate basis is required.");
        }

        if (string.IsNullOrWhiteSpace(rendererBasisVersion))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Renderer basis version is required.");
        }

        return Result.Success();
    }

    private const string SelectPagesSql =
        """
        select
            page_id as PageId,
            document_instance_id as DocumentInstanceId,
            page_index as PageIndex,
            page_label as PageLabel,
            width as Width,
            height as Height,
            rotation as Rotation,
            coordinate_basis as CoordinateBasis,
            basis_width as BasisWidth,
            basis_height as BasisHeight,
            renderer_basis_version as RendererBasisVersion,
            source_file_hash as SourceFileHash,
            created_at as CreatedAt,
            updated_at as UpdatedAt
        from pages
        """;

    private static object ToParameters(Page page)
    {
        return new
        {
            PageId = page.PageId.ToString(),
            DocumentInstanceId = page.DocumentInstanceId.ToString(),
            page.PageIndex,
            page.PageLabel,
            page.Width,
            page.Height,
            page.Rotation,
            page.CoordinateBasis,
            page.BasisWidth,
            page.BasisHeight,
            page.RendererBasisVersion,
            page.SourceFileHash,
            CreatedAt = FormatUtc(page.CreatedAt),
            UpdatedAt = FormatUtc(page.UpdatedAt)
        };
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static Result<T> DatabaseFailure<T>(Exception exception)
    {
        return Result<T>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
    }

    private sealed class PageRow
    {
        public string PageId { get; set; } = string.Empty;
        public string DocumentInstanceId { get; set; } = string.Empty;
        public int PageIndex { get; set; }
        public string? PageLabel { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public int Rotation { get; set; }
        public string CoordinateBasis { get; set; } = string.Empty;
        public double? BasisWidth { get; set; }
        public double? BasisHeight { get; set; }
        public string RendererBasisVersion { get; set; } = string.Empty;
        public string? SourceFileHash { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;

        public Page ToPage()
        {
            return new Page(
                LiteratureApp.Core.Ids.PageId.Parse(PageId),
                LiteratureApp.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
                PageIndex,
                PageLabel,
                Width,
                Height,
                Rotation,
                CoordinateBasis,
                BasisWidth,
                BasisHeight,
                RendererBasisVersion,
                SourceFileHash,
                DateTimeOffset.Parse(CreatedAt),
                DateTimeOffset.Parse(UpdatedAt));
        }
    }
}
