# TODO

Ideas parked for later. Not commitments.

## Evals
- [ ] **Repository variables for defaults.** `LLM_ENDPOINT` / `LLM_MODEL` in repo settings, used when a run doesn't pick a preset.
- [ ] **Scheduled runs.** Weekly cron, plus a run when the prompt, tool descriptions or `evals/` change. Mind free-tier quotas.
- [ ] **Results table in the README.** Pass rates per model (hosted vs local) once clean runs exist.
- [ ] **Trend tracking.** Keep reports over time and chart pass rate per model and prompt version (fits the web UI dashboard).

## Agent API
- [ ] **Identity:** OAuth (Keycloak in compose), who asked / who approved in the audit trail, no self-approval.
- [ ] **Multi-tenancy:** per-tenant ERP connection, tool policy and audit.
- [ ] **OpenTelemetry:** one trace API → agent → LLM → MCP → DB, Aspire dashboard in compose.
- [ ] **Scale-out:** lease-based run claiming (`FOR UPDATE SKIP LOCKED`) for several instances.
- [ ] **Thin web UI:** chat, approval cards, live audit timeline, eval dashboard.

## Agent and MCP
- [ ] **Server-side approval via MCP elicitation**, so approval holds even with Claude Desktop as the client.
- [ ] **Tamper-evident audit:** hash-chain the audit log.
- [ ] **Odoo adapter** (open source, runs in Docker): real-ERP integration without vendor licensing.
- [ ] **Gemini native client** instead of the OpenAI-compatible layer, if thought signatures keep causing friction.

## Presentation
- [ ] Repo description and topics (`mcp`, `ai-agents`, `dotnet`, `erp`).
- [ ] Demo GIF: approval flow in the CLI or UI.
- [ ] Link from the portfolio site and CV; case study page.
- [ ] LinkedIn post: "agents that fail closed".
