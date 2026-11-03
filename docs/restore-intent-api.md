# Human-only API: неисполняемый restore intent

## Отдельная граница от agent authority

POST `/api/test-environments/{id}/restore-intents` принимает только ожидаемые stage job/artifact IDs. Это запрос на durable metadata hold, а не команда restore. Требуются cookie-аутентификация человека и CSRF token; AgentToken не является допустимой заменой. Текущий owner/admin доступ и все пины повторно проверяет SQL application service под locks.

Запрос по форме:

```json
{
  "expectedStageJobId": "00000000-0000-0000-0000-000000000001",
  "expectedArtifactId": "00000000-0000-0000-0000-000000000002"
}
```

В теле нельзя назначать SQL/IB names, canonical mappings, incarnation, paths или grant flags. Receipt содержит только неисполняемые корреляционные пины и `executable: false`; это не свидетельство владения внешней IB.

## Включение только явной конфигурацией

По умолчанию сервис disabled. Для будущего operator-managed включения нужны `RestoreIntent:Enabled` и `RestoreIntent:CanonicalMappings` для конкретного pool. Mapping — доверенная конфигурационная декларация canonical IDs, не физическое обнаружение aliases и не доказательство ownership. Неизвестные/несогласованные mappings отклоняются. Конфигурация валидируется до начала обслуживания запросов даже при disabled: некорректные GUID/неизвестные поля mapping не пропускаются молча. Стандартный configuration binder не поддерживает GUID dictionary keys; поэтому ключи сначала читаются как строки и отдельно нормализуются/проверяются, а не через Dictionary<Guid,...> binding. Никаких production значений в репозиторий не добавлено, на стенде флаг не включается автоматически.

В данном срезе нет UI-кнопки, agent claim, executable restore grant/lease, Worker registration или фактического restore. Необратимые retained holds снимаются только будущим отдельно спроектированным reconciliation, не истечением stage lease. Оператор должен учитывать, что intent заблокирует retirement/cleanup/release этого artifact.

## Проверки

HTTP boundary tests используют fake application service и тестовую user authentication scheme, не настоящий Identity login: они проверяют CSRF, запрет anonymous/AgentToken, передачу actor и пинов, default-off и безопасные ответы отказа. Production cookie-аутентификация следует существующей общей конфигурации. Отдельные SQL opt-in HTTP tests используют настоящий Identity login, secure/HttpOnly cookie, свежий CSRF и реальный SQL service: owner request/replay, missing/pre-login CSRF, foreign owner, anonymous и AgentToken-only отрицательные сценарии. Их результат фиксируется после реального SQL прогона, а не выводится из fake boundary tests. SQL-корректность и взаимное исключение cleanup проверяются отдельно реальным SQL набором. Подмена fake-сервиса не является доказательством исполнения restore или подключения к рабочей базе.

Первичный HTTP прогон обнаружил неверное тестовое предположение о времени применения WebApplicationFactory config; options теперь читаются из окончательного IConfiguration через singleton factory и принудительно разрешаются до начала traffic. Проверки также обнаружили unsupported Guid-key binder и закрепили явный разбор строковых ключей. Эти исправления не включают feature по умолчанию.

Локальный полный Release: **657 passed / 0 failed**, SQL131 skipped без explicit opt-in; locked restore success, diff check success. Новый fake HTTP/config subset23passed, включая real default-off service409 без SQL connection, malformed canonical mappings и unknown authority body fields. Полный Windows SQL2019 прогон: **131 passed / 0 failed / 0 skipped**, включая все3 новых real Identity cookie HTTP cases. Суммарно788 .NET cases; реальные cookies/CSRF проверены через HTTPS TestServer, не IIS/network end-to-end. Включение сервиса происходило только в disposable test host. TRX retained отдельно TestRuns/restore-intent-cookie-20261103; рабочая API DB не мигрировалась.

Изменения исходников не публикуют API в IIS и не запускают migration: это отдельные действия, которые здесь не выполняются.
