# Целевая архитектура репозитория

## Принцип
Сделать **spec-first слой** отдельным от production-кода и не смешивать спецификации продукта с документацией реализации. Не переносить все проекты под новые абстрактные слои вроде `Domain/Application/Infrastructure`: здесь границы в первую очередь соответствуют публикуемым библиотекам.

## Предлагаемое дерево

```text
/
├── .github/
│   └── workflows/                 # CI остаётся здесь
├── docs/
│   ├── architecture/              # контекст, контейнеры, зависимости
│   ├── decisions/                 # ADR (либо ссылки на specs/decisions)
│   ├── development/               # build/test/release/contributing
│   └── repository/                # карта дерева, соглашения
├── specs/
│   ├── 000-project-baseline/
│   │   ├── spec.md
│   │   ├── acceptance.md
│   │   └── plan.md
│   ├── 001-<feature-slug>/
│   │   ├── spec.md
│   │   ├── acceptance.md
│   │   ├── plan.md
│   │   └── tasks.md
│   └── decisions/                 # если ADR централизованы в specs
├── templates/
│   ├── spec.md
│   ├── plan.md
│   └── tasks.md
├── checklists/
│   ├── definition-of-ready.md
│   ├── definition-of-done.md
│   └── repository-move.md
├── build-sh/                      # сохранить существующие скрипты до отдельного решения
├── src/
│   ├── Sa.slnx
│   ├── Directory.Packages.props
│   ├── Directory.Build.props     # если реально используется/появится
│   ├── Libraries/
│   │   ├── Sa/
│   │   ├── Sa.Configuration/
│   │   ├── Sa.Configuration.PostgreSql/
│   │   ├── Sa.Data.PostgreSql/
│   │   ├── Sa.Data.S3/
│   │   ├── Sa.Data.TempFolder/
│   │   ├── Sa.Outbox/
│   │   ├── Sa.Outbox.PostgreSql/
│   │   ├── Sa.Partitional.PostgreSql/
│   │   ├── Sa.Schedule/
│   │   ├── Sa.HybridFileStorage/
│   │   ├── Sa.HybridFileStorage.FileSystem/
│   │   ├── Sa.HybridFileStorage.S3/
│   │   ├── Sa.HybridFileStorage.Postgres/
│   │   ├── Sa.Media/
│   │   ├── Sa.Media.FFmpeg/
│   │   └── Sa.Utils.WorkQueue/
│   ├── Samples/
│   └── Tests/
├── README.md
├── LICENSE
└── [root build/config files retained unless explicitly migrated]
```

## Важная оговорка о `src/Libraries`
Это целевая **опция**, а не обязательное условие SDD. Если сейчас библиотеки уже лежат непосредственно в `src/`, самый безопасный первый этап — оставить физические пути без изменений. Переносить в `src/Libraries/` только отдельным PR после проверки относительных ссылок и build graph. Если дополнительная вложенность не даёт реальной пользы, оставьте текущую плоскую структуру `src/<PackageName>`.

> **Статус:** перенос выполнен по SPEC-001 (`specs/001-src-libraries-layout/`): все 17 library-проектов,
> включая непубликуемую `Sa`, находятся в `src/Libraries/`. `src/Samples`, `src/Tests` и
> `src/Benchmarks` остались на месте.

## Правила размещения
- `specs/`: нормативные требования, сценарии, acceptance criteria, ограничения и планы изменений.
- `docs/`: объяснения и справочная документация; не дублирует нормативные требования из specs.
- `src/`: проекты, входящие в сборку/публикацию.
- `src/Samples/`: примеры потребления библиотек.
- `src/Tests/`: тесты. Сохранять явную связь тестового проекта с библиотекой.
- `build-sh/`: скрипты разработчика/CI. Не менять путь в рамках документационного PR.
- `.github/workflows/`: workflow GitHub Actions.
- `templates/`: только шаблоны рабочих документов, не спецификации реальных функций.

## Нейминг specs
`NNN-short-kebab-case`, стабильный ID после создания. Не переиспользовать номер удалённой/отменённой спецификации. Изменение требований — отдельный коммит/PR и запись в changelog spec или decision log.

## Правила зависимостей
1. Проекты не должны ссылаться на `specs/` при сборке.
2. Проекты библиотек не ссылаются на samples/tests.
3. Samples ссылаются на опубликованный/проектный API библиотек; не копируют внутреннюю реализацию.
4. Тесты ссылаются на тестируемый пакет и только на необходимые test utilities.
5. Изменение публичного API или NuGet metadata требует явного acceptance criterion.
