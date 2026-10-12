# Аудит 3.2.0 и чистка репозитория (ветка cleanup-3.3)

- Date: `2026-10-13T12:00:00Z`
- Type: `maintenance`
- Status: `partial`
- Related: `reports/2026-10-13 Аудит и план улучшений 3.3/PLAN.md`

## What changed

- Проведён аудит бэкенда, интерфейса, точности и репозитория; план — в `reports/2026-10-13 Аудит и план улучшений 3.3/PLAN.md`.
- `artifacts/` сокращён с 12,2 до 2,1 ГБ; канонический набор моделей GigaAM — `artifacts/models` (MANIFEST.json с sha256); архив свидетельств — `artifacts/archive/evidence-2026-10-13.zip`.
- Автозапуск включён по умолчанию: настройка `autostart`, `AutostartService.EnsureEnabled` восстанавливает запись Run при старте, dev-сборки не регистрируются.
- Удалены устаревшие установщики и скрипты (React/Electron, Full preview, web-bootstrap, релизы 2.0/2.2).
- Удалены Whisper, гибридный движок, детектор смешанной речи, режимы Direct/смешанной речи, пакеты Whisper.net*, лицензии Whisper/CUDA.
- Бенчмарк корпуса переведён на `RussianSpeechQualityService`, отчёт schema v4 (v2/v3 читаются).

## Why

Владелец: быстрый запуск, малое потребление ресурсов, высокая точность, чистый репозиторий; второго движка нет.

## How

Коммиты `3ed3dc8`, `124a53f`, `bbce2d6`, `7b9c2ef`, `ffb319a`, `0335fe1`; тег `pre-whisper-removal` на `124a53f`.

## Verification

| Check | Result | Evidence |
| --- | --- | --- |
| dotnet test | 1177 из 1177 | локальный прогон 13.10.2026 |
| Сборка sln и Compact | без ошибок; Whisper.net.dll исчез из вывода Compact | локальная сборка |
| Хеши моделей | совпали с docs/models/*.json | MANIFEST.json |

## Contract impact

- Architecture: unchanged для Compact-пути; Full-ветка и Whisper удалены (ARCHITECTURE.md содержит исторические описания).
- App map: updated.
- Roadmap: unchanged, этапы 0–4 плана аудита впереди.
- Specs/tickets: unchanged.

## Risks and next action

- Не проверено: живая диктовка после чистки, установка на чистую Windows, реальный запуск на 144 Гц.
- Следующее: запись корпуса `quality-challenge-v1` голосом, затем этапы 0–2 плана (профиль 9% CPU, параллельный декодинг, ReadyToRun, перерисовка рамки капсулы).
- Qwen и Translator не тронуты (решения владельца нет).

## Files

См. `git log cleanup-3.3`.
