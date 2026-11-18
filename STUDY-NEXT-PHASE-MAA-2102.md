# MAA-2102 next phase: implementation and safety study

## Request

A standalone .NET system with its own SQL Server database that manages incoming and outgoing messages (text, audio, video) for several WhatsApp numbers, with API methods so other web systems can post outgoing messages and read incoming ones.

## What the POC already covers

| Part | Status |
|---|---|
| Own SQL Server database with outbox and inbound tables | Done, text only |
| API to queue a message and to read incoming messages | Done in the lab, no authentication yet |
| Pacing: send hours, daily cap, wave limit, gap, random jitter | Done, for one number |
| Swappable WhatsApp connector (an interface with a fake and a real one) | Done |
| Several numbers at the same time | Not done: one number is one browser profile and one worker |
| Audio and video | Not done |
| API authentication, access per system, delivery status | Not done |
| Running on a server | Not tested |

## Target design

![fig:1]

The ERP becomes one client of the API. Each number has its own worker, login and pacing. Messages and results go through the database, so a worker restart does not lose queued messages.

## Connector options

| Option | What it is | Media | Notes |
|---|---|---|---|
| Browser automation (current: Playwright and Edge) | Drives WhatsApp Web like a person | Text proven; audio and video not tested | One browser per number. Memory per session is not measured; no reliable public figure was found. Breaks when the page changes (4 fixes in one day in the POC) |
| Baileys (TypeScript) | Talks to WhatsApp Web over WebSockets; login by QR or pairing code | Library READMEs describe sending audio, video and voice notes and downloading media | Not .NET, so it would run as a separate service. Maintainers state they do not condone use that breaks WhatsApp's terms. Version 7.0.0 had breaking changes |
| whatsmeow (Go) | Go client for the multi-device API | README lists media as supported | Not .NET, so a separate service |
| .NET ports of Baileys: Baileys.NET 1.9.1, Dawa.WhatsApp | C# ports | Not checked | Baileys.NET: .NET 10, MIT, updated 25 Apr 2026, 2.2K downloads in total. Dawa.WhatsApp README says early stage. Completeness and upkeep are not verified |
| Ready-made NuGet port of whatsapp-web.js (WhatsApp.Client) | Tried in the POC | Not applicable | Failed: WhatsApp Web's content security policy blocked its script |

Recommendation: keep the current connector for text (proven) and keep the interface, so a second connector can be added. In Phase 0, test one protocol-level connector for media and several numbers, and choose by test results, not by documentation. Both kinds are unofficial.

## Implementation notes (proposed, not built yet)

| Requirement | How |
|---|---|
| Several numbers | Session table (number, state, login location, settings). One worker per session with its own login folder or auth state, own pacing and daily cap. QR login per number from the dashboard or API. Never the same number in two places |
| Send media | Outbox row points to a media record. The worker uploads the file through the connector. Size and type limits |
| Receive media | The worker downloads the file and stores it outside the database (disk or blob). The database keeps type, size, hash and path. Failed downloads are retried and flagged |
| Post outgoing | POST with number, target group or contact, text or media id and an optional idempotency key. Returns an id. GET status: Pending, Sent, Failed |
| Read incoming | GET incoming after a cursor, filtered by number. The client confirms what it processed, so nothing is read twice or lost. Webhook as a later option |
| Process layout | Workers run as their own service, not inside the ERP. The API stays up when a worker restarts |

## Safety: account (ban risk)

What the sources say: WhatsApp's Terms of Service name bulk messaging, auto-messaging and automated account creation as prohibited. The Messaging Guidelines (updated 23 Sep 2026) list unofficial clients, bulk messaging and automation under adversarial behaviour, with the qualifier "to harm WhatsApp or our users", and say an account can be suspended temporarily or permanently. Both connector types are unofficial, so neither removes the risk. Vendor articles say bans are often abrupt and without warning; they come from companies that sell the official API, so treat them as indicative only. No public rule on safe send rates was found. The POC settings (10 to 15 a day, waves) come from team advice and are not verified.

| Measure | Why |
|---|---|
| Use dedicated numbers, not personal numbers | A ban then costs only that number (POC lesson: the linked number was personal) |
| Send only to groups and people who expect messages | The Guidelines name repeated unwanted contact as spam |
| Pacing per number: hours, daily cap, wave, gap, jitter | Avoids machine-like bursts (implemented) |
| One worker per number, no second login of the same number | Avoids conflicting sessions |
| Do not open and close the connection in quick cycles | One vendor article lists it as a suspicious pattern (single source) |
| Kill switch per number; pause automatically on logout or repeated failures | Stops before damage grows |
| Monitor sends, failures and logouts per number | Finds problems early |

## Safety: the system

| Area | Measure |
|---|---|
| API access | API key per calling system, stored hashed, limited to its allowed numbers. HTTPS. Rate limit per key |
| Login material | The browser profile or auth state is the key to the account: encrypted disk, restricted access, never in git, zips or shared backups |
| Secrets | Connection strings and keys outside the repository (vault or environment) |
| Private data | Group messages are private. Per-number allow list of groups to read (POC lesson: receiving stored messages from an unrelated group), retention period, access log |
| Media files | Size and type limits, malware scan, never executed, stored outside the web root |
| Audit | Log who posted what, to which number and when. Message content is not written to application logs |
| Resilience | The outbox lives in the database; a worker crash loses nothing; retry up to 3 times (implemented) |

## Phase 0 proof plan

| # | Test | Done when |
|---|---|---|
| 1 | Several numbers in parallel | 3 numbers linked and sending; memory and CPU per session measured |
| 2 | Send audio and video | One audio and one video delivered to a test group |
| 3 | Receive audio and video | Media from another number stored with correct type, size and hash |
| 4 | Run on a server | Worker runs without a window for several days; login survives restarts |
| 5 | Second connector | Same tests with one protocol-level library, compared with the browser connector |
| 6 | API and security | Two clients with separate keys; each sees only its own numbers |

Every test gets a log entry in the results file. No estimate is given until these are done.

## Sources

| Source | Link |
|---|---|
| WhatsApp Terms of Service (effective 4 Jan 2021) | https://www.whatsapp.com/legal/terms-of-service |
| WhatsApp Messaging Guidelines (updated 23 Sep 2026) | https://www.whatsapp.com/legal/messaging-guidelines |
| Baileys | https://github.com/WhiskeySockets/Baileys |
| whatsmeow | https://github.com/tulir/whatsmeow |
| Baileys.NET (NuGet) | https://www.nuget.org/packages/Baileys.NET |
| Dawa.WhatsApp (NuGet) | https://www.nuget.org/packages/Dawa.WhatsApp |
| Vendor article on unofficial API risk (indicative only) | https://www.bot.space/blog/whatsapp-api-vs-unofficial-tools-a-complete-risk-reward-analysis-for-2025 |
