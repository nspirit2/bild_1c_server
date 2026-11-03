# Read-only RAC inventory: наблюдение имени, не разрешение CREATE

## Цель

Перед будущей попыткой создания новой цели необходимо наблюдать, занято ли ожидаемое имя. Успешный полный список IB позволяет сказать только «имя наблюдалось/не наблюдалось в этом ответе». Он не резервирует имя, не исключает другого администратора, не подтверждает ownership/пустоту/инкарнацию и не разрешает CREATE/restore/delete.

В этом срезе — только bounded pure parser и regression tests. Никаких регистраций Worker/StepRegistry, RAC adapter, journal transitions или server executable grants. Ранее добавленный scheduled-jobs observer решает другую задачу: не смешиваем наличие IB с политикой запрета заданий.

## Проверенный формат источника

Read-only authenticated `rac infobase summary list` на Windows/1С8.3.22.2557 вернул поля infobase/name/descr и blank separator. Для подтверждения grammar выводились только имена полей, не содержимое/credentials. Сервисы не перезапускались, IB не изменялась. PowerShell потребовал explicit System.Security assembly для DPAPI read; первый read-only запрос остановился до RAC, после загрузки assembly завершился успешно.

## Fail-closed правила

Полный exit0, отсутствие truncation и bounded text/record/field sizes. Неизвестный формат/поле, duplicate UUID или case-insensitive duplicate name, control chars, missing identity или overflow отвергают **весь** список. Нельзя делать вывод об отсутствии из неполного ответа. Descr не отдаётся потребителю. Case-only вариант ожидаемого имени (BS_TEST_... вместо bs_test_...) отклоняется как ambiguous target, а не ошибочно возвращает Absent. Exact Present тоже не идентифицирует владельца. Boundary whitespace в имени не должен нормализовать потенциально отличную identity в exact Present: parser ограничен однозначной строкой имени, неоднозначные padding/control формы отклоняются. Empty complete success может означать observed absence, но не permission.

Scope связывает ожидаемые operation/fence/target correlation/canonical cluster/endpoint mapping/observation пины и reservation-derived имя. Эти пины задаёт вызывающий код; parser не аутентифицирует labels, не доказывает свежесть ответа и не предотвращает same-scope replay. Будущий adapter должен иметь trusted endpoint mapping и фиксированный read-only argv; SQL presence observer дополнительно обязан проверять metadata visibility — скрытая DB не равна отсутствующей.

## Review и тесты

Независимое review не обнаружило false-Absent пути на malformed/duplicate/missing/case-only данных, но отметило нормализацию boundary whitespace имени. Поведение ужесточено: снимается только один стандартный ASCII separator после colon; surrounding whitespace у имени и любые controls исходного value отвергаются. Это сохраняет exact-name semantics, не предполагая, что 1С эквивалентно сравнивает пробелы или aliases.

Filtered Release inventory53/53passed; полный локальный Release **710passed/0failed**, SQL134 explicitly skipped без opt-in; locked restore и diff check successful. SQL suite последний раз прошёл134/134 на WindowsSQL2019 в предыдущем status-срезе; этот pure parser SQL не изменяет. Node42passed в status-срезе. Новых package/migration/model изменений нет.

## Обязательная политика restore

Любой будущий restore остаётся под обязательным scheduled-jobs-deny=on до/после restore и после restart. Security profile не требуется. Inventory не заменяет эти проверки, durable ownership history, server fence/lease или reconciliation неизвестного результата.
