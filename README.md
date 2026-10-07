# nabo-recipes

Отдельный ASP.NET Core 10 API каталога и редактора рецептов Nabo. PostgreSQL — существующая общая БД; сервис владеет только схемой `nabo_recipes`. Основной Nabo API продолжает обслуживать авторизацию, поиск товаров и корзины.

## Возможности

- 41 существующий рецепт импортируется при первом запуске, вместе с ингредиентами, фото-ссылками, КБЖУ, шагами и атрибуцией источников.
- Создание и редактирование черновиков, отдельная публикация, снятие с каталога через `recipe.isActive=false` и публикацию.
- Для ингредиента отдельно задаются название для пользователя, `searchQuery` для поиска, `exactName` и `exactUnit` для точного подбора. Эти поля передаются фронтом основному retail API; сервис рецептов сам товары не подбирает.
- Оптимистическая блокировка: устаревшая `revision` возвращает 409. Изменение черновика не меняет опубликованный рецепт.
- Фото пока только по HTTPS URL. Файлы в контейнер не загружаются: Railway filesystem не является постоянным хранилищем фото. Позже можно подключить S3/R2 без изменения структуры рецепта.

## Авторизация

Используется существующий Bearer-токен Nabo: сервис проверяет его запросом `GET AUTH_API_BASE_URL/api/auth/me`. Токен не расшифровывается локально и не сохраняется. Право редактора определяется серверной таблицей `nabo_recipes.recipe_editors` по ID пользователя, а не номером телефона из браузера. Гость получает 401, обычный пользователь — 403; сбой авторизации или БД — 503.

`RECIPE_ADMIN_PHONE` нужен только для первичного назначения редактора из **уже существующей** строки `public.users`. Если редакторов нет и аккаунт не зарегистрирован, сервис откажется запускаться с понятной ошибкой. После назначения права привязаны к ID; перерегистрация того же номера их не переносит. Сервис не создаёт и не сбрасывает пароли. Необходимо зарегистрировать служебный аккаунт в основном Nabo до выкладки и войти им на фронте.

## API

Машиночитаемая документация: `/openapi/v1.json`.

| Метод | Адрес | Доступ / результат |
|---|---|---|
| GET | `/health` | Проверка БД и версии схемы |
| GET | `/api/recipe-catalog` | Публичный `{documents:[{recipe,ingredients}]}`; без черновиков |
| GET | `/api/admin/recipes/access` | Только редактор, `{authorized:true}` |
| GET | `/api/admin/recipes` | Только редактор, `{records:[{slug,document,revision,published}]}` |
| PUT | `/api/admin/recipes/{slug}` | Только редактор; `{document,revision,publish}` → `{revision}` |

`revision=0` создаёт новый рецепт. Для обновления передать последнюю revision из списка. `publish=true` одновременно сохраняет и публикует. `publish=false` сохраняет только черновик. Удаление физически не предусмотрено: отключите isActive и опубликуйте. Каталог возвращает и неактивные опубликованные документы, чтобы фронт не восстанавливал их из старого локального каталога.

Документ содержит `recipe` текущей модели Nabo и `ingredients` — словарь только его ингредиентов. Примеры полной модели находятся в `src/Data/catalog.json`. Ограничения: до 20 ингредиентов, до 100 шагов, 256 KiB на запрос, slug до 100 символов, запрос поиска до 160, точное название до 500. `source`, `sourceUrl` и лицензии существующих рецептов сохраняются.

## Railway: в том же проекте

1. В существующем **проекте и environment** Nabo выберите **New → GitHub Repo → yanafront/nabo-recipes**. Добавьте новый сервис; основной сервис и Postgres не заменять. Railway обнаружит Dockerfile. Root Directory — корень репозитория; отдельные Build/Start commands не нужны.
2. В Variables нового сервиса задайте (замените `Postgres` реальным именем сервиса БД):

```dotenv
DATABASE_URL=${{Postgres.DATABASE_URL}}
AUTH_API_BASE_URL=https://naboback-production.up.railway.app
RECIPE_ADMIN_PHONE=+375295495775
PORT=8080
```

Для приватного соединения к БД можно использовать явные ссылки, если DATABASE_URL в вашем шаблоне указывает на публичный TCP proxy:

```dotenv
DATABASE_URL=Host=${{Postgres.RAILWAY_PRIVATE_DOMAIN}};Port=5432;Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}}
```

Используйте именно ту БД, в которой основной API хранит `public.users`. Пользователь БД должен иметь CREATE SCHEMA, права на собственную схему и SELECT на public.users для первичного назначения. Сервис не изменяет public.users и не запускает миграции основного API. Перед первой выкладкой сделайте резервную копию БД.

3. Для auth API можно оставить указанный HTTPS URL. Альтернатива внутри того же environment:

```dotenv
AUTH_API_BASE_URL=http://${{ИМЯ_ОСНОВНОГО_API.RAILWAY_PRIVATE_DOMAIN}}:${{ИМЯ_ОСНОВНОГО_API.PORT}}
```

Сначала проверьте фактический PORT основного API. Vercel не имеет доступа к Railway private network; для фронта нужен публичный URL нового сервиса.

4. Нажмите Deploy. При запуске сервис транзакционно создаёт собственные таблицы и один раз импортирует рецепты. Advisory lock защищает параллельные реплики. Healthcheck `/health` настроен в railway.json. Перезапуски не перезаписывают ваши рецепты.
5. **Settings → Networking → Generate Domain**, target port 8080. Проверьте `<домен>/health` и `<домен>/api/recipe-catalog`.
6. На Vercel фронта nabo-new добавьте серверные переменные и выполните Redeploy:

```dotenv
NUXT_RECIPES_API_BASE=https://НОВЫЙ-ДОМЕН.up.railway.app
NUXT_MANAGED_RECIPES_ENABLED=true
```

`NUXT_RETAIL_API_BASE` остаётся адресом основного API. Перед включением переменных нужно выложить обновлённый фронт, в котором присутствует `recipesApiBase`. Редактор: `/admin/recipes`; войдите служебным аккаунтом. Ссылка в публичное меню не добавляется. Нельзя ограничивать доступ только скрытым URL: серверная проверка редактора обязательна и реализована.

Для дополнительного редактора вручную, после проверки аккаунта:

```sql
INSERT INTO nabo_recipes.recipe_editors(user_id)
SELECT id FROM public.users WHERE phone_number = '+375XXXXXXXXX'
ON CONFLICT DO NOTHING;
```

Отзыв доступа: `DELETE FROM nabo_recipes.recipe_editors WHERE user_id = '<UUID>';`. После первичного назначения можно удалить RECIPE_ADMIN_PHONE из Railway variables, чтобы управление редакторами было только явным.

## Локальная проверка

.NET SDK 10 и PostgreSQL. `.env.example` — пример, ASP.NET не загружает .env автоматически: экспортируйте переменные в shell.

```sh
dotnet build src/Nabo.Recipes.csproj -c Release
dotnet run --project tests/Nabo.Recipes.Checks.csproj
dotnet run --project src/Nabo.Recipes.csproj
```

Для проверки конфликтов/публикации на реальной БД задайте `TEST_DATABASE_URL` на **отдельную одноразовую тестовую БД** и повторите checks. Этот режим удаляет только её схему nabo_recipes; никогда не указывайте production DATABASE_URL.

В CI выполняются build и checks, включая PostgreSQL integration на отдельном контейнере. API не включает CORS: браузер обращается через серверные Nuxt endpoints; token берётся из HTTP-only session cookie, записи защищены проверкой Origin на фронте.

Документация Railway: [variables](https://docs.railway.com/variables), [private networking](https://docs.railway.com/networking/private-networking), [Dockerfiles](https://docs.railway.com/builds/dockerfiles).
