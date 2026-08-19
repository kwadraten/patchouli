# Do Not Use Native AOT

Status: accepted

Patchouli desktop and MCP releases use self-contained CoreCLR publishing without Native AOT or trimming. Native AOT publication can currently complete on Windows, but successful linking does not establish runtime correctness for Patchouli's dependency graph. Avalonia reflection bindings and DataGrid, Dapper runtime row mapping, PDFiumCore and its native payloads, embedded ASP.NET Core endpoints, and reflection-based JSON call sites all produce trimming or AOT analysis warnings. Shipping those artifacts would move failures from build time to user-only code paths without enough product benefit to justify that risk.

Release packaging must therefore keep `PublishAot` and `PublishTrimmed` disabled. AOT and trimming analyzers may still be run as diagnostic tools, but their output is not a release gate and analyzer success must not be confused with support for an AOT release. This decision can be reconsidered only when there is a concrete product need, target-platform smoke coverage exists for the complete application, and the relevant runtime warnings have been removed or explicitly validated.

Patchouli application code uses `System.Text.Json` as its JSON implementation. The repository does not directly reference or call Newtonsoft.Json. PDF processing is provided by the low-level PDFiumCore adapter and does not define the application's JSON implementation. Do not add direct Newtonsoft.Json usage.

**Considered Options**

- Self-contained CoreCLR publishing without trimming.
- Native AOT publishing after suppressing or accepting current warnings.
- Trimmed CoreCLR publishing.
