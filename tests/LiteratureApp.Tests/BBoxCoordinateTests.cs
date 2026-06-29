using FluentAssertions;
using LiteratureApp.Core.Bibliography;
using LiteratureApp.Core.Documents;
using LiteratureApp.Core.Layout;
using LiteratureApp.Core.Time;
using LiteratureApp.Infrastructure.Bibliography;
using LiteratureApp.Infrastructure.Coordinates;
using LiteratureApp.Infrastructure.Documents;
using LiteratureApp.Infrastructure.Layout;
using LiteratureApp.Infrastructure.LibraryIdentity;
using LiteratureApp.Infrastructure.Migrations;
using LiteratureApp.Ocr;

namespace LiteratureApp.Tests;

public sealed class BBoxCoordinateTests
{
    [Fact] public async Task ConvertNormalized_valid_bbox_returns_same() { await using var c=await Context.CreateAsync(); var r=await c.Service.ConvertToNormalizedPageAsync(c.PageId,new(.1,.2,.3,.4,SourceBBoxCoordinateSystem.NormalizedPage)); r.IsSuccess.Should().BeTrue(); r.NormalizedBBox!.Value.X.Should().Be(.1); }
    [Fact] public async Task ConvertNormalized_out_of_range_fails() { await using var c=await Context.CreateAsync(); (await c.Service.ConvertToNormalizedPageAsync(c.PageId,new(.9,.2,.2,.2,SourceBBoxCoordinateSystem.NormalizedPage))).ErrorCode.Should().Be(BBoxErrorCodes.OutOfBounds); }
    [Fact] public async Task ConvertImagePixels_converts_to_normalized() { await using var c=await Context.CreateAsync(); var r=await c.Service.ConvertToNormalizedPageAsync(c.PageId,new(10,20,30,40,SourceBBoxCoordinateSystem.ImagePixels,100,200)); r.NormalizedBBox!.Value.Should().Be(new NormalizedBBox(.1,.1,.3,.2)); }
    [Fact] public async Task ConvertImagePixels_missing_basis_fails() { await using var c=await Context.CreateAsync(withBasis:false); (await c.Service.ConvertToNormalizedPageAsync(c.PageId,new(1,1,2,2,SourceBBoxCoordinateSystem.ImagePixels))).ErrorCode.Should().Be(BBoxErrorCodes.BasisMissing); }
    [Fact] public async Task ConvertUnknownBasis_fails_bbox_coordinate_transform_failed() { await using var c=await Context.CreateAsync(); (await c.Service.ConvertToNormalizedPageAsync(c.PageId,new(1,1,2,2,SourceBBoxCoordinateSystem.Unknown))).ErrorCode.Should().Be(BBoxErrorCodes.TransformFailed); }
    [Fact] public async Task ConvertZeroWidthOrHeight_fails() { await using var c=await Context.CreateAsync(); (await c.Service.ConvertToNormalizedPageAsync(c.PageId,new(0,0,0,.2,SourceBBoxCoordinateSystem.NormalizedPage))).ErrorCode.Should().Be(BBoxErrorCodes.Invalid); }
    [Fact] public async Task ConvertNaN_fails() { await using var c=await Context.CreateAsync(); (await c.Service.ConvertToNormalizedPageAsync(c.PageId,new(double.NaN,0,.2,.2,SourceBBoxCoordinateSystem.NormalizedPage))).ErrorCode.Should().Be(BBoxErrorCodes.Invalid); }
    [Fact] public async Task ValidateBBox_rejects_negative_values() { await using var c=await Context.CreateAsync(); var result=await c.Service.ConvertToNormalizedPageAsync(c.PageId,new(-.1,0,.2,.2,SourceBBoxCoordinateSystem.NormalizedPage)); result.ErrorCode.Should().Be(BBoxErrorCodes.OutOfBounds); }

    private sealed class Context : IAsyncDisposable
    {
        private Context(TemporarySqliteDatabase db, PageCoordinateService service, Core.Ids.PageId pageId){Database=db;Service=service;PageId=pageId;}
        public TemporarySqliteDatabase Database{get;} public PageCoordinateService Service{get;} public Core.Ids.PageId PageId{get;}
        public static async Task<Context> CreateAsync(bool withBasis=true)
        {var db=TemporarySqliteDatabase.Create();var clock=new FixedClock(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));await new MigrationRunner(db.ConnectionFactory,TestPaths.MigrationsDirectory).RunAsync();var lib=new LibraryIdentityService(db.ConnectionFactory,clock);await lib.CreateLibraryAsync("bbox");var item=await new ItemService(db.ConnectionFactory,lib,clock).CreateItemAsync("book","bbox");var doc=await new DocumentInstanceService(db.ConnectionFactory,clock).AttachDocumentInstanceAsync(item.Value.ItemId,null,DocumentInstanceType.PrimaryScan);var page=await new PageService(db.ConnectionFactory,clock).CreatePageAsync(doc.Value.DocumentInstanceId,0,null,null,null,0,CoordinateBasis.NormalizedPage,withBasis?100:null,withBasis?200:null,"test",null);return new Context(db,new PageCoordinateService(db.ConnectionFactory),page.Value.PageId);}
        public ValueTask DisposeAsync()=>Database.DisposeAsync();
    }
}
