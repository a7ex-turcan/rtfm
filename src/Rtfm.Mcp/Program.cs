// Rtfm.Mcp — stdio MCP server exposing search_docs (built in Phase 4).
//
// IMPORTANT (CLAUDE.md §2.2): once implemented, this process speaks the MCP
// protocol on STDOUT. Every diagnostic MUST go to STDERR — a single stray
// Console.Out write corrupts the transport and the server silently fails to
// connect. Hence the placeholder below writes only to stderr.
Console.Error.WriteLine("rtfm-mcp: not implemented yet (Phase 4).");
return 0;
