# MAA-2102 POC report: WhatsApp group messaging from .NET


## Summary

What was built: a .NET service that uses a WhatsApp number through WhatsApp Web. The number is linked once by scanning a QR code on a dashboard page. No official Business API is used.

What works, tested with real WhatsApp: sending to groups of 4, 5, 29 and 36 members, scheduled sending, queueing a message from an API call, from a SQL Server insert and from a Hangfire cron job, and reading new group messages into SQL Server. 47 messages were sent during the test, and Hangfire ran 26 jobs with 0 failed.

What is still open: the risk of the number being banned over time, running on a server, and a bug in receiving (see Findings).

## Approach

| Item | Choice |
|---|---|
| Connection | Playwright drives Edge on WhatsApp Web; QR scanned once from the dashboard, login kept in a local profile |
| Sending | All triggers write a row to an outbox table in SQL Server; one dispatcher sends it |
| Pacing | Send window 08-18, daily cap, wave limit, minimum gap with random jitter |
| Receiving | Loop reads the chat list, opens groups with new messages, stores them in an Inbound table |
| Mode | Runs without a visible browser window |

## Key terms

| Term | Meaning |
|---|---|
| Playwright | Library that controls a browser from code; here it drives Edge on WhatsApp Web like a person at the screen |
| Outbox | SQL Server table that holds every message to send, with its status |
| Dispatcher | Background service that sends due outbox rows and applies the pacing rules; the only sender |
| Hangfire | Background job library that stores jobs in SQL Server; here it runs the recurring announcement (cron) and only writes outbox rows |

## Design

![fig:1]
![fig:2]

## Screenshots

Before login: the dashboard shows a QR; scan it from WhatsApp, Linked devices.

![img:poc/fig-qr.png]

After login: state is Linked; after a normal restart no new QR is needed.

![img:poc/fig-linked.png]

Outbox: every message with its status and source (Api, SqlTrigger, Scheduled).

![img:poc/fig-outbox.png]

Hangfire, recurring job: cron `0 2 * * *` (UTC) runs `AnnouncementJob.Run`, which only writes an outbox row.

![img:poc/fig-hf-recurring.png]

Hangfire, executed jobs: 26 succeeded, 0 failed.

![img:poc/fig-hf-jobs.png]

SQL Server data: outbox counts, received messages and stored member counts.

![img:poc/fig-sql.png]

API console log (excerpt): member counts read and messages sent.

![img:poc/fig-log.png]

## Result per ticket item

| Ticket item | Result | Evidence |
|---|---|---|
| Send to a joined group | Proven | Message sent to test group "Advancer Wa test"; shown on the phone |
| Group with 30 members | Proven | Messages were sent to a 36-member group (1) and a 29-member group (3); member counts are read from the group info panel and stored |
| Scheduled announcements | Proven | One-off message sent after its due time; Hangfire recurring cron job wrote an outbox row and the message was sent |
| Receive and process group messages | Proven, with limits | Three messages from a second number were stored once each. Known bug: 14 messages sent by the lab account itself were also stored as incoming |
| Trigger from SQL Server | Proven | Direct insert into the outbox table, sent about 5 seconds later |
| Trigger from API | Proven | `POST /api/whatsapp/messages` |
| Trigger from background service | Proven | Hangfire job plus the dispatcher service |

## Findings

| Finding | Impact |
|---|---|
| Unofficial automation breaks WhatsApp terms | The number can be banned; use a number that can be lost |
| Depends on the WhatsApp Web page layout | The receive reader needed four fixes in one day after the page differed from assumptions; expect maintenance |
| One number needs one worker | In the ERP this should be a separate worker process |
| Opening a group to read it marks it as read on the phone | Side effect of receiving |
| Receiving reads every synced group, not only the test group | 4 messages from an unrelated group were stored before receiving was switched off; it needs a per-group allow list |
| The sync also lists some one-to-one chats as groups (0 members) | A test message could be queued to a person by mistake; the list needs a filter |
| Login was lost once after the process was stopped by force | A new QR scan was needed after the restart; cause not confirmed; the worker must stop cleanly |
| Group list changes only when a sync is run | After switching the WhatsApp account, run the sync; the new account's 18 groups then appeared |
| One send timed out (message box not found in 8 seconds) | The retry succeeded; the dispatcher's retry handled it |
| Pacing held messages as designed | Messages stay Pending outside the send window or when the wave is full |

## Not yet proven

| Item | Needed |
|---|---|
| Many messages arriving at once | Test with a burst from another number |
| New QR scan while hidden | Test in hidden mode |
| Run on a server | Headless hosting and a stable session on a server |
| Long run with pacing | Several days of operation |
| ERP integration | Exploration of the ERP (background jobs, table placement) |
