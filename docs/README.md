# Sa — документация и SDD

Репозиторий развивается **spec-first**: нормативные требования и критерии приёмки живут в
[`specs/`](../specs), шаблоны рабочих документов — в [`templates/`](../templates), гейты review — в
[`checklists/`](../checklists). Здесь, в `docs/`, лежат только объяснительные материалы: они не
дублируют требования из `specs/`.

## С чего начать

| Что нужно | Куда смотреть |
|---|---|
| Правила процесса (spec → plan → tasks → implement → verify) | [`development/sdd-workflow.md`](development/sdd-workflow.md) |
| Текущие инварианты и baseline | [`../specs/000-project-baseline/spec.md`](../specs/000-project-baseline/spec.md) |
| Фактическое дерево, зависимости, path-dependent конфигурация | [`repository/project-structure.md`](repository/project-structure.md) |
| Результаты build/test/package на baseline | [`../specs/000-project-baseline/baseline.md`](../specs/000-project-baseline/baseline.md) |
| Архитектурные решения (ADR) | [`decisions/`](decisions) |
| Правила сборки/тестов/упаковки | [`../AGENTS.md`](../AGENTS.md) |

## Как применять

1. Прочитать [`repository/project-assessment.md`](repository/project-assessment.md) — первичную
   оценку рисков (написана до baseline-замеров, см. оговорку в файле).
2. Принять или поправить [`architecture/target-architecture.md`](architecture/target-architecture.md).
3. Сверить baseline-спеку с фактическим деревом из [`repository/project-structure.md`](repository/project-structure.md).
4. Выполнять миграцию небольшими вертикальными этапами по
   [`development/migration-plan.md`](development/migration-plan.md).
5. Для каждой задачи создавать spec → plan → tasks, реализовывать, проверять acceptance criteria и
   только затем закрывать задачу.

## Структура документации

```text
docs/
├── README.md                     этот индекс
├── NuGet.md                      payload пакета (PackageReadmeFile) — путь зашит в packaging props
├── architecture/                 контекст и целевая структура
├── development/                  процесс SDD и план миграции
├── decisions/                    ADR
└── repository/                   оценка и инвентарь репозитория
```

`docs/NuGet.md` физически привязан к `src/Common.NuGet.Properties.xml`
(`<None Include="..\..\docs\NuGet.md" …>`) — его нельзя перемещать без правки packaging props.

## Нормативные артефакты (корень репозитория)

- [`specs/`](../specs) — спецификации и acceptance criteria (`specs/000-project-baseline/` — текущий baseline).
- [`templates/`](../templates) — шаблоны feature spec, implementation plan и task breakdown.
- [`checklists/`](../checklists) — DoR/DoD и миграционный чек-лист.

## Термины

SDD здесь означает Spec-Driven Development: спецификация поведения и критерии приёмки предшествуют
плану реализации и коду.
