# План перехода к SDD и реорганизации

## Этап 0 — baseline (без перемещения исходников)
- Зафиксировать текущую ветку/commit и дерево файлов.
- Запустить `./build-sh/do-build.sh`, `./build-sh/do-test.sh`, `./build-sh/do-package.sh` на чистом checkout; сохранить результаты.
- Записать известные ограничения и flaky/integration tests.
- Добавить `specs/`, `templates/`, `checklists/` и эти правила.
- Обновить корневой README ссылкой на workflow и specs.

**Выход:** SDD доступен команде, production-пути не изменены.

**Статус: выполнен** (commit `2b422c9`; результаты — `specs/000-project-baseline/baseline.md`).

## Этап 1 — архитектурный baseline
- Заполнить `specs/000-project-baseline/spec.md` фактами из репозитория.
- Построить карту библиотек и зависимостей из `.csproj`.
- Записать ADR о текущем layout и критерии, по которым его можно менять.
- Зафиксировать package IDs, TFMs, namespaces, public API и Native AOT constraints.

**Выход:** есть проверяемый список инвариантов.

**Статус: выполнен** (`specs/000-project-baseline/spec.md`, `docs/repository/project-structure.md`).

## Этап 2 — документация
- Перенести/сгруппировать только Markdown-документацию, если это не ломает ссылки.
- Создать `docs/repository/project-structure.md` с деревом, которое реально генерируется из checkout.
- Сохранить исходные ссылки через обновление всех relative links.
- Проверить README на GitHub после PR.

**Выход:** документация согласована; код не перемещён.

**Статус: выполнен** (docs сгруппированы: `architecture/`, `development/`, `decisions/`, `repository/`; `NuGet.md` оставлен на месте, т.к. путь зашит в packaging props).

## Этап 3 — выборочная структурная миграция
Для каждого move:
1. Создать отдельную spec и ADR/decision, если меняются архитектурные границы.
2. Сгенерировать точный список перемещаемых файлов.
3. Обновить ссылки в `.slnx`, `.csproj`, props/targets, Dockerfiles, scripts, workflow, docs.
4. Запустить build, unit tests, integration tests и package.
5. Сравнить NuGet package metadata и публичный API с baseline.
6. Не совмещать массовое форматирование или функциональные изменения с move.

**Выход:** один понятный PR на ограниченный набор перемещений.

**Статус: выполнен для библиотек** — SPEC-001 перенёс 17 library-проектов в `src/Libraries/`,
SPEC-003 сгруппировал их по доменам (`Configuration/`, `Data/`, `Hybrid/`, `Media/`, `Outbox/`),
оставив `Sa`, `Sa.Partitional.PostgreSql`, `Sa.Schedule`, `Sa.Utils.WorkQueue` в корне
(`specs/001-src-libraries-layout/`, `specs/003-libraries-domain-groups/`).

## Этап 4 — закрепить SDD в процессе
- Для новой функциональности PR ссылается на spec ID.
- Требования и acceptance criteria согласуются до implementation plan.
- План разбит на небольшие проверяемые задачи.
- CI проверяет формат/наличие обязательных секций spec (автоматизацию добавлять отдельной задачей).
- Документация и spec обновляются вместе с изменением поведения.

## Порядок проверки каждого PR
1. `dotnet restore src/Sa.slnx -c Release`
2. `dotnet build src/Sa.slnx -c Release -v n`
3. `dotnet test src/Sa.slnx -v n`
4. `./build-sh/do-package.sh`
5. Проверить интеграционные тесты с PostgreSQL/Minio/Testcontainers в окружении CI.
6. Сравнить список пакетов и их metadata с baseline.
7. Проверить все пути в README и документации.

Команды взяты из публичного README; перед использованием подтвердить их соответствие текущей ветке.

## Откат
- Каждый этап отдельным PR/commit.
- Не смешивать переименование каталогов с API changes.
- При регрессии откатывать только структурный PR.
- Не удалять старые `specs/`, пока новые ссылки не проверены и миграция не принята.
