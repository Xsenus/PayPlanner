# Реестр отчетов PayPlanner

Дата проверки: 2026-08-09

## Итог

- Семейств отчетов на экране: 34.
- Отчетов через единый управленческий экспорт: 33.
- Отдельных сводных отчетов: 1, «Юридическая работа».
- Форматы для каждого семейства: CSV, Excel, PDF и HTML-печать.
- Общие права: просмотр и экспорт раздела `reports`.
- Дополнительные ограничения применяются к платежным отчетам, задачам и административному отчету импортов.

## Юридические и задачные отчеты

| Отчет | API данных | Экспорт | Дополнительная область видимости |
|---|---|---|---|
| Юридическая работа | `/api/reports/legal-work` | `/legal-work.csv`, `/legal-work.xls`, `/legal-work.pdf`, `/legal-work.print` | Дела, задачи, счета |
| Финансы по делам | `/api/reports/case-finance` | `management` | Дела, счета |
| Успешность дел | `/api/reports/case-success` | `management` | Дела |
| Текущие дела | `/api/reports/current-cases` | `management` | Дела, задачи |
| Загрузка юристов | `/api/reports/lawyer-workload` | `management` | Дела, задачи |
| Просроченные процессуальные сроки | `/api/reports/overdue-deadlines` | `management` | Дела |
| Активность по делам | `/api/reports/case-activity` | `management` | Дела |
| Задачи по журналу | `/api/reports/tasks` | `management` | Задачи и отдельное право экспорта задач |

## Денежные отчеты

| Отчет | API данных | Ключ единого экспорта |
|---|---|---|
| Движение денежных средств | `/api/reports/cash-flow` | `cash-flow` |
| Прогноз движения денежных средств | `/api/reports/cash-flow-forecast` | `cash-flow-forecast` |
| Касса | `/api/cash-orders/cash-book` | `cash` |
| Банк | `/api/reports/cash-flow` с источником Bank | `bank` |
| План-факт | `/api/reports/plan-fact` | `plan-fact` |
| Авансы | `/api/reports/advances` | `advances` |
| Приоритеты платежей | `/api/reports/payment-priorities` | `payment-priorities` |
| Оплаченные счета | `/api/reports/paid-accounts` | `paid-accounts` |
| Оплаченные накладные/счета-фактуры | `/api/reports/paid-invoices` | `paid-invoices` |

Для денежных отчетов дополнительно проверяются область видимости платежей и право их экспорта.

## Документы, статусы и аналитические разрезы

| Отчет | API данных | Ключ единого экспорта |
|---|---|---|
| Статусы счетов | `/api/reports/invoice-statuses` | `invoice-statuses` |
| Статусы актов | `/api/reports/act-statuses` | `act-statuses` |
| Статусы договоров | `/api/reports/contract-statuses` | `contract-statuses` |
| Категории | `/api/reports/categories` | `categories` |
| Контрагенты | `/api/reports/counterparties` | `counterparties` |
| Проекты | `/api/reports/projects` | `projects` |
| Направления | `/api/reports/directions` | `directions` |
| Точки продаж | `/api/reports/sales-points` | `sales-points` |
| НДС | `/api/reports/vat` | `vat` |
| Платежные поручения | `/api/reports/payment-orders` | `payment-orders` |
| Назначения платежей | `/api/reports/payment-purposes` | `payment-purposes` |
| Статусы сопоставления | `/api/reports/match-statuses` | `match-statuses` |
| Статусы журнала | `/api/reports/journal-statuses` | `journal-statuses` |
| Валюты | `/api/reports/currencies` | `currencies` |
| Кассовые ордера | `/api/reports/cash-orders` | `cash-orders` |
| Импорт банковских выписок | `/api/reports/bank-statement-imports` | `bank-statement-imports` |
| Табличные импорты | `/api/reports/spreadsheet-imports` | `spreadsheet-imports` |

Отчет табличных импортов дополнительно ограничен администраторами.

## Единые маршруты экспорта

- CSV: `/api/reports/management.csv`.
- Excel: `/api/reports/management.xls`.
- PDF: `/api/reports/management.pdf`.
- Печать: `/api/reports/management.print`.

Во все четыре маршрута передаются одинаковые фильтры периода, источника, счета, контрагента, клиента, дела, договора, статуса, ответственного, даты среза и лимита. UI-флаг запрета экспорта удаляется перед формированием запроса и не попадает в API.

## Автоматические доказательства

- `ReportsControllerTests.ManagementReportExports_ReturnCsvExcelPdfAndPrintFiles` последовательно формирует четыре формата для всех 33 ключей единого экспорта.
- Профильные тесты проверяют область видимости и дополнительные права задач, платежей, кассовых ордеров и импортов.
- `ReportsControllerTests.LegalWork_GroupsVisibleCasesDeadlinesTasksAndFinance` проверяет данные и все четыре формата сводного юридического отчета.
- Frontend-реестр `MANAGEMENT_REPORT_KINDS` содержит все доступные ключи, включая восстановленный отчет `tasks`.

