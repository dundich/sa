# Первичная оценка проекта

> **Оговорка.** Документ написан до baseline-замеров и основан на публичном README. Там, где он
> расходится с фактическим checkout, верны `repository/project-structure.md` и
> `../specs/000-project-baseline/` (например, тестовых проектов 17, а не 15; S3-фикстура поднимает
> SeaweedFS, а не Minio).

## Что подтверждается доступной документацией
`Sa` — набор переиспользуемых инфраструктурных библиотек для .NET 10, заявлена совместимость с Native AOT и nullable reference types. README перечисляет библиотеки для конфигурации, S3/временных каталогов, transactional outbox, PostgreSQL partitioning, планировщика, гибридного файлового хранилища, WAV/FFmpeg и async work queue. Также заявлены samples, 15 тестовых проектов на xUnit v3 и Testcontainers, централизованное управление версиями пакетов через `Directory.Packages.props`, решение `src/Sa.slnx` и shell-скрипты сборки в `build-sh/`.

Источник: https://github.com/dundich/sa (README на момент подготовки набора).

## Архитектурные наблюдения
1. Это скорее **репозиторий библиотек/monorepo**, чем одно приложение. Публичные пакеты должны оставаться независимо обозримыми и тестируемыми.
2. Основные оси организации уже понятны: production libraries, samples, tests, build tooling, project-level docs.
3. Риск реорганизации выше обычного: `.csproj`/`.slnx`, `Directory.Build.*`, `Directory.Packages.props`, Docker/Testcontainers, shell scripts, README-ссылки и CI могут зависеть от путей.
4. Перемещение проектов не должно менять публичные NuGet ID, namespace, API, target frameworks, packing rules или поведение сборки без отдельной спецификации.
5. Нужна инвентаризация фактического дерева, включая скрытые каталоги и workflow. README не доказывает, что все перечисленные каталоги/проекты существуют именно в текущей ветке.

## Рекомендация
Не начинать с массового перемещения. Сначала закрепить текущие контракты и baseline, затем разделить «документационную реорганизацию» и «перемещение исходников». В первом PR добавить SDD-структуру и правила; следующие PR — по одному ограниченному изменению структуры с зелёными build/test/package.

## До первого PR проверить
- [ ] `git status --short` чистый.
- [ ] Полный список файлов и проектов текущей ветки сохранён как артефакт.
- [ ] Базовые команды build/test/package проходят на чистом checkout.
- [ ] CI workflows, Dockerfiles, shell scripts и ссылки README перечислены.
- [ ] Список всех `.csproj`, `.slnx`, `Directory.*.props/targets`, `global.json`, `NuGet.config` зафиксирован.
- [ ] Публичные NuGet package IDs и целевые framework-версии сохранены.
