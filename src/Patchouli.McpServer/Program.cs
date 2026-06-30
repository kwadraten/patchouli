using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.McpServer;

if (args.Contains("--help")) { Console.Error.WriteLine("Patchouli MCP stdio server: --db <runtime.sqlite>"); return; }
var index=Array.IndexOf(args,"--db"); if(index<0||index==args.Length-1){Console.Error.WriteLine("Missing --db.");return;}
try { var db=new SqliteConnectionFactory(args[index+1]); var clock=new SystemClock(); var library=new LibraryIdentityService(db,clock); await new MigrationRunner(db,Path.Combine(AppContext.BaseDirectory,"migrations")).RunAsync(); var profiles=new SearchProfileService(db,library,clock); var api=new McpReadApi(db,new SqliteSearchService(db,profiles),new EvidenceReferenceService(db,clock)); var handler=new McpProtocolHandler(api,db); string? line; while((line=await Console.In.ReadLineAsync()) is not null){var response=await handler.HandleAsync(line);await Console.Out.WriteLineAsync(response);await Console.Out.FlushAsync(); if(line.Contains("\"shutdown\""))break;} } catch(Exception ex){Console.Error.WriteLine(McpOutputSanitizer.Sanitize(ex.Message));}
