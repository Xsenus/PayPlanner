# Автоматическая доставка PayPlanner — 25 октября 2026

**Итог попытки: CI завершён с `failure`, установка не выполнялась.** Проверенный локально код `fe4cda5c31e218d3ca164b4e4f918c71e9847b9c` отправлен обычным push в `main`; [Actions 40181241823, attempt 1](https://github.com/Xsenus/PayPlanner/actions/runs/40181241823) запущен 09:35 UTC и завершён после последнего выполнявшегося job в 10:27:54 UTC. Два независимых отказа окружения требуют устранения. Успешного CI и нового deployment этот документ не заявляет.

## Фактические задания CI

| Job | Результат | Проверенный итог |
|---|---|---|
| backend-build | failure | Setup .NET, strict restore, reference-cache contracts, build с анализаторами и discovery успешны; `Upload compiled test runtime` заблокирован квотой GitHub. |
| frontend | failure | Статические проверки и аудит успешны; 2 unit-теста не смогли создать file symlink. Integration/build/upload не выполнялись. |
| backend-format | success | Полная исходная команда форматирования решения: step/job success, 717 с (11:57); числовой exit в минимальном логе отдельно не напечатан. |
| backend-test | skipped | Matrix не развернулась; API вернул один skipped placeholder. |
| backend-browser | skipped | Не запускался. |
| backend | skipped | Общая квалификация CI не запускалась. |
| deploy | skipped | Установка и production readiness этого запуска не выполнялись. |

В этой попытке API содержит **7 записей: 1 success / 2 failure / 4 skipped**. При полном выполнении обязательны 7 definitions / 10 instances с четырьмя shard. Локальные 9882+5 не выдаются за выполненные тесты CI.

## Ошибка хранения GitHub

`backend-build`, job `121132581772`, 09:55:59.788 UTC: `Failed to CreateArtifact: Artifact storage quota has been hit.` В сообщении указан пересчёт использования каждые 6–12 часов. Read-only API репозитория подтвердил **0 артефактов**; cache count/size также 0 на момент отдельной проверки. Чужие репозитории, Packages, платёжные настройки и сохранённые доказательства не очищались.

Self-hosted вычисления и квота хранения — разные ограничения. GitHub описывает общее хранилище artifacts/Packages и отдельную квоту cache; удаление текущих объектов не отменяет уже накопленное использование. [Правила GitHub](https://docs.github.com/en/billing/concepts/product-billing/github-actions).

Пользователю передан выбор: восстановить квоту/оплату GitHub либо использовать отдельное хранилище на существующем XSENUS. Ответ ещё не получен. Замена хранилища не реализована. Внешний review-only план: `E:/ProjectTemp/PayPlanner/20261025-delivery/xsenus-artifact-store-proposal-01/PLAN.md`; manifest SHA256 `40aca8f77ec1aba4c93442b467d8ef70634c15d7257cfe5c24aeb23dd1b88c09`. Его patch не готов к применению: helper/validator/tests ещё отсутствуют, сохранение matrix outputs при rerun failed требует отдельного подтверждения. Все проверки происхождения, SHA/SDK/manifest, полный каталог, строгий TRX union и обязательные зависимости должны сохраняться.

Компиляция CI с анализаторами прошла за **804 с (13:24)**, tool build — за 8 с; 0 ошибок, одно CS9124 в `TaskBulkBillingTests.cs:623`. Каталог: **9882 случая / 4432 метода**, части **2522 / 2483 / 2375 / 2502**, lanes **5536 fast / 3770 heavy / 576 exclusive**. CI manifest SHA256 `61f9730585cae782af5ee210314e280a083658eaf3a10828aa2b822dd6926adb` отличается от локального: это другая сборка и собственная область доказательств. Успешная discovery не означает успешный test run.

## Ошибка Windows file symlink

`frontend`, job `121132581986`, `Test FE guard and unit`: две ошибки `EPERM` при `fs.symlink(..., 'file')` в `scripts/verification-inputs.test.mjs:141` и `:164`, до проверок поведения. Guard **70/70**, остальные unit **3061 passed / 2 failed**; суммарно **3131 passed / 2 failed из 3133**. Копия, hardlink, junction вместо file symlink либо skip этих случаев не исправляют исходные проверки.

Lint, typecheck, даты, кодировка, surface и npm audit (0 vulnerabilities) прошли. Integration, legal readiness, production build и artifacts были пропущены после unit failure. Права точного процесса runner ещё не измерены: сама EPERM не различает отсутствующую привилегию, её состояние и другие ограничения Windows.

Подготовлена проверенная read-only диагностика: `E:/ProjectTemp/PayPlanner/20261025-delivery/xsenus-symlink-diagnostic-02.ps1` и `xsenus-symlink-diagnostic-02-README.md`; script SHA256 `638f30e87895298841481fb53be00073587b26914d69bc277df6f40f1dfa59a6`. Один smoke на ILYA завершился actual exit 0 с настоящей file symlink, проверкой байтов и очисткой. Это **не проверка XSENUS**. Скрипт не меняет права, ACL, Developer Mode, службу или реестр; версия01 и её неуспех сохранены.

Для продолжения запрошен существующий способ администрирования XSENUS либо выполнение диагностики пользователем в настоящем контексте runner. Имя XSENUS разрешается в tailnet `100.66.201.67`; SSH22 не ответил, WinRM5985/5986 недоступны, SMB445 доступен, но административная D$ не доступна с текущей авторизацией. Пароли не подбирались, системная политика не менялась. Если привилегии нет, её назначение конкретной учётной записи и свежий logon требуют отдельного решения; запуск всей CI с Administrator или глобальный Developer Mode автоматически не применяются.

## Локальная квалификация и доставка

Полный backend локально прошёл **9882/9882 + restored 5/5**, actual helper/owner/process exit 0, **9458.297 с (2:37:38)**. Полный frontend — **3552/3552**, без пропусков; форматирование/strict restore/сборка успешны. [Сводка и proof hashes](payplanner-handoff-2026-10-25.md). Этот результат остаётся привязан к прежним входам; изменённый transport/workflow нельзя квалифицировать переписыванием старого digest.

Артефактов этого CI — **0**. SSH-ключ GitHub, transport нового deploy и VPS post-deploy проверка в фактическом CI не достигнуты. Последняя ранее подтверждённая установленная ревизия — `9448c76f`; новых production-изменений в этой попытке нет. Подготовленный `vps-after-deploy-01.py` ожидает fe4 и **не запускался**. После исправления причин обязательны успешные pre-deploy jobs, штатный deployment ожидаемого SHA и полный успешный CI, затем внешние health/frontend/service-worker/revision/readiness и прямая проверка VPS с сопоставлением квалифицированных файлов.

Доказательства этой попытки хранятся вне Git: `E:/ProjectTemp/PayPlanner/20261025-delivery/ci-40181241823/`. Backend job log SHA256 `1cb15e26d956ee588f0d1a52176bab1c00b386fc35d35f35f9fcf1404524f58d`, frontend job log `246bb0ce243b74c37ebe51c40e14c4878c8526cf0b6bbb512377acbc3eb1f9c7`. Итоговый отчёт CI: `final-report-01.json`, SHA256 `045bf3b3a4228747baaa7b1c9ffa0b84034636a77100f98dbe990bdcb00004d6`. Диагностика/секреты/бинарные пакеты в Git не добавляются. Следующий документационный commit отличается от SHA кода и не является развёртыванием.
