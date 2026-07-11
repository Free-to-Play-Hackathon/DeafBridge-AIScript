# Accessibility Communication Assistant MVP

Production-style MVP backend for finalized transcript intake, AI analysis, proposed actions, confirmation, reminders, and email delivery.

## Stack

- .NET 8 ASP.NET Core Web API
- EF Core + PostgreSQL
- RabbitMQ + MassTransit
- Worker service for consumers, outbox, reminders
- SendGrid email integration
- xUnit tests
- Docker Compose

## Local Env

Copy `.env.example` to `.env` and fill local secrets. Do not commit `.env`.

Required for JWT:

```text
JWT_SECRET=replace-with-at-least-32-random-characters
JWT_ISSUER=UavPms.Api
JWT_AUDIENCE=UavPms.Client
JWT_EXPIRY_MINUTES=60
```

Optional for real email:

```text
SENDGRID_API_KEY=
SENDGRID_FROM_EMAIL=noreply@example.com
SENDGRID_FROM_NAME=Accessibility Assistant
```

For real agentic tool calling, set OpenAI as the AI provider:

```text
AI_PROVIDER=openai
OPENAI_API_KEY=your-openai-api-key
AI_MODEL=gpt-5.6
```

The OpenAI agent uses the Responses API with function tools:

- `save_note`
- `create_task`
- `create_appointment`
- `schedule_reminder`
- `send_email`

The model decides which tool to call. The backend executes those tool calls by creating `ProposedAction` records. Appointment, reminder, task, and email actions still require user confirmation before the worker creates the real appointment/reminder/email work.

With `AI_PROVIDER=fake`, backend uses deterministic fake agent for offline tests.
For Groq-backed JSON behavior, set:

```text
AI_PROVIDER=groq
AI_API_KEY=your-groq-api-key
AI_MODEL=llama-3.3-70b-versatile
```

For Cohere-backed JSON behavior, set:

```text
AI_PROVIDER=cohere
COHERE_API_KEY=your-cohere-api-key
AI_MODEL=command-r7b-12-2024
```

## Docker

Full stack:

```bash
docker compose up --build
```

Services:

- API: http://localhost:8080
- Swagger: http://localhost:8080/swagger/index.html
- RabbitMQ UI: http://localhost:15672
- PostgreSQL: localhost:5432

Worker applies EF migrations on startup, then runs consumers, outbox processor, and reminder scheduler.

## Test

Local tests require the .NET 8 runtime/SDK installed on the machine running tests.
If the host only has .NET 9/10, run tests through Docker instead.

These may take longer than 45 seconds after target/package changes:

```bash
dotnet restore Accessibility.slnx
dotnet test Accessibility.slnx --no-restore
docker compose build
```

Docker test command:

```bash
docker compose --profile test run --rm tests
```

API smoke test after starting Docker Compose:

```powershell
docker compose down -v
docker compose up -d --build
.\scripts\smoke-test.ps1
```

`docker compose down -v` removes the local PostgreSQL volume. Use it for clean demo validation, not when preserving local data matters.

## Demo Seed

On API startup, demo seed is enabled by default:

```text
DEMO_SEED_ENABLED=true
```

It creates:

- User: `local@example.com`
- Conversation: `22222222-2222-2222-2222-222222222222`
- One HearingUser transcript telling the deaf user to attend a hospital appointment, bring an insurance card, and receive a reminder
- One outbox event so the worker/Groq agent processes it automatically

Use this Swagger endpoint to inspect the seeded conversation:

```text
GET /api/conversations/22222222-2222-2222-2222-222222222222/proposed-actions
```

## Example Flow

1. Create conversation.
2. Submit finalized transcript.
3. Outbox publishes `TranscriptReceived`.
4. Worker asks the AI agent to call backend tools.
5. Read proposed actions.
6. Confirm appointment/reminder actions.
7. Outbox publishes confirmation.
8. Worker creates appointment/reminder.
9. Reminder scheduler publishes email work when due.

Example transcript body:

```json
{
  "speaker": "HearingUser",
  "originalText": "Ngay mai luc 8 gio toi co lich kham o benh vien Cho Ray, hay nhac toi truoc 30 phut.",
  "language": "vi",
  "startedAt": "2026-07-11T10:00:00+07:00",
  "endedAt": "2026-07-11T10:00:05+07:00",
  "sequenceNumber": 1
}
```

## Architecture

```mermaid
flowchart LR
    Whisper["Local Whisper"] --> API["Accessibility.Api"]
    API --> DB[("PostgreSQL")]
    API --> Outbox["OutboxMessages"]
    Worker["Accessibility.Worker"] --> DB
    Worker --> Rabbit["RabbitMQ"]
    Rabbit --> Agent["AgentAnalysisConsumer"]
    Agent --> DB
    Rabbit --> Router["ConfirmedActionRouterConsumer"]
    Router --> Actions["Create note/task/appointment/reminder"]
    Actions --> DB
    Worker --> Reminder["ReminderScheduler"]
    Reminder --> Email["SendGrid"]
```
