# Слог 3.3.1: сторож хука молчит в играх, честная подмена кнопки, диагностика распознавания

- Date: `2026-10-27T12:00:00Z`
- Type: `fix`
- Status: `partial`
- Related: `none` (ветка `release-3.3.1`)

## What changed

- Сторож хука Mouse 5 не переустанавливает хук, пока на переднем плане игра и кнопка не удерживается (новый вердикт `GameForeground`).
- Если запрошенное сочетание не подключилось (например, Ctrl+Alt+Space занято), показывается уведомление в трее и пометка «Сейчас: …» в Центре управления; настройка владельца не перезаписывается.
- В лог пишется строка `Recognition breakdown` (очередь, прогрев, CPU, загрузка системы, GC) без текста.
- Центр управления: флажки и ползунок в стиле Слога, строка истории на сетке, подпись «Сейчас:» пересчитывается при смене темы.

## Why

Логи за неделю: 46 ложных переустановок хука (44 в FC27, 1 в WoW, 1 в explorer, все «old hook was alive»), молчаливая подмена привязки, 13 всплесков времени распознавания. Решение владельца: в играх Mouse 5 принадлежит игре.

## How

`HookWatchdog.Evaluate` получил последний необязательный параметр `foregroundIsGame`; `MousePushToTalkService.CheckHookHealth` учитывает игру на любом краю окна сторожа. Остальное — `ActivationBindingInfo.FallbackNotice`, `TrayNotificationKind.ActivationFallback`, `RecognitionDiagnostics`.

## Verification

| Check | Result | Evidence |
| --- | --- | --- |
| `dotnet test` | 1212 пройдено, 0 упало | запуск 27.10.2026 |
| Независимое ревью (Fable) | блокеров нет | пункты 1–4, 8, 10–12 ниже в рисках |
| Лог: foreground ложных переустановок | 45 из 46 — игры по `IsGame` | `slog_all.log` |
| Снимки UI (тёмная, светлая, контраст) | просмотрены | render-settings-preview |

## Contract impact

- Architecture: unchanged, правки внутри существующих сервисов
- App map: unchanged
- Roadmap: unchanged
- Specs/tickets: unchanged

## Risks and next action

- Не проверено живьём: игра, уведомление при занятом сочетании (ошибка 1409), смена темы в запущенном приложении.
- Окно, не опознанное как игра (RDP, Parsec, эмуляторы), сохраняет старое поведение сторожа.
- Настоящая смерть хука сразу после выхода из игры обнаруживается с задержкой до ~6 с.
- Граница окна сторожа не вынесена в чистую функцию и не покрыта тестом.
- Запись `Recognition breakdown` синхронная, на горячем пути (доли миллисекунды).

## Files

`Services/PushToTalkInput.cs`, `Services/MousePushToTalkService.cs`, `Services/ActivationSettingsService.cs`, `Services/TrayService.cs`, `Services/TrayNotificationPolicy.cs`, `Services/RecognitionDiagnostics.cs`, `Services/RussianSpeechQualityService.cs`, `MainWindow.xaml.cs`, `SettingsWindow.xaml`, `SettingsWindow.xaml.cs`, тесты.
