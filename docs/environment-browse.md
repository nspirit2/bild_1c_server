# Bounded история окружений

GET `/api/test-environments/browse?limit=25&cursor=...` возвращает `{items,nextCursor,observedAt}`. Items — существующие EnvironmentView (id,projectId,displayName,state,expiresAt), без новых source paths, секретов или authority flags. Старые GET list/detail/request сохраняются без изменений; новая UI-панель использует только bounded browse.

## Границы и авторизация

Limit по умолчанию25, допустимо1..100. Разрешены только одиночные query parameters limit/cursor; пустые, неизвестные/повторные и неверные значения отвергаются400. Cursor ограничен512 base64url символами и канонической closed JSON формой версии1, с EnvironmentId, ActorId, Administrator. Он привязан к actor/role и позиции, но не подписан и НЕ является полномочием: пользователь может сформировать свою позицию; доступ определяется текущим SQL predicate заново на каждой странице.

Видимость точно совпадает со старой историей: administrator OR owner с текущим ProjectMembership. Нет нового фильтра project.Enabled, lifecycle или срока. Наследуется существующая cookie role policy; новое чтение не вводит обещание current database role revalidation для администраторов. Machine bearer не заменяет human cookie. Все обычные API ответы no-store/nosniff по существующему middleware.

Один serializable read transaction, AsNoTracking, SQL Take(limit+1), не полная материализация коллекции. Lowercase GUID-D text с BIN2 collation используется и для ORDER BY, и для keyset predicate — SQL uniqueidentifier order не подменяет текстовый cursor order. Отображаемое имя ограничивается в SQL до120 UTF-16 units. Read-only transaction не пишет audit/lifecycle и не обращается к физическим целям. Take ограничивает ответ/материализацию, но не гарантирует ограниченную стоимость SQL scan/sort: текстовая конверсия GUID может требовать сортировки; измеренный query plan/index performance этим срезом не заявляется.

Cursor и observedAt не образуют snapshot всей цепочки страниц: записи/права могут меняться между запросами. Обновление возвращает к первой странице. Истёкшие/потерянные права не сохраняются cursor. No total count: NextCursor присутствует только при дополнительной видимой строке в этом снимке. Нет произвольного offset или обещания стабильного номера записи при конкурентных изменениях.

## UI

«Мои окружения» читает по25 записей с кнопками назад/вперёд, явным номером страницы и временем наблюдения. Нет фонового polling, storage cursor или автоматического retry. Refresh сбрасывает cursor stack. Новый переход очищает прежние сведения; их отдельный GET по-прежнему проверяет id/projectId. Все прежние generation/auth/navigation/visibility clear guards сохранены. Ошибка cursor не зацикливается — результат очищается, можно явно обновить сначала.

Это история окружений, не collection API запросов; request/job/source correlation пока не добавлена. Literal state не доказывает physical Ready. Нет TTL/delete/cancel/renew/create/restore действий или выдачи подключения.

## Проверки

SQL CI проверяет bounded pages/keyset order и отсутствие overlap, owner isolation/admin visibility, текущие права между страницами. Offline/API fixtures проверяют строгие limit/cursor/actor semantics и metadata schema без provider/реального стенда. Node fixtures проверяют bounded page contract, forward/back/refresh, malformed cursor/page, stale transport/body, auth и clearing. Полный локальный Release:2436passed/0failed,275SQLexplicitlyskipped; Node76passed/0failed. Новые API22cases и offline cursor validation1passed;3SQLcases требуют disposable database CI. Skipped local SQL не считается passed; CI фиксируется отдельно.