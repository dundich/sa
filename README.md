# sa

dot net10 experimental aot project


## [Sa.Outbox.PostgreSql](src/Sa.Outbox.PostgreSql)

Designed for implementing the Outbox pattern using PostgreSQL, which is used to ensure reliable message delivery in distributed systems. It helps prevent message loss and guarantees that messages will be processed even in the event of failures.

- Reliable message delivery: Ensures that messages are stored in the database until they are successfully processed.
- Parallel processing: Enables messages to be processed in parallel, increasing system performance.
- Flexibility: Supports various types of messages and their handlers.
- Tenant support: Allows for even distribution of load.
- Data cleaning: scheduled deletion of old data.

## [Sa.Partitional.PostgreSql](src/Sa.Partitional.PostgreSql)

A library designed for managing table partitioning in PostgreSQL with the aim of improving performance and manageability for large volumes of data.

- Declaratively describe a partitioned table by time (day, month, year).
- Define partitions based on lists of keys for rows or numbers.
- Set a schedule for migrations to create new partitions.
- Set a schedule for deleting old partitions.
- Manage partitions.

## [Sa.Schedule](src/Sa.Schedule)

`Sa.Schedule` provides a way to configure and execute tasks on a schedule.

- It allows you to manage a set of tasks that will be executed at specific times or at defined intervals.
- You can start and stop tasks.
- Define failure strategies: close the application, stop job, stop all jobs, or ignore the failure.

## [Sa.HybridFileStorage](src/Sa.HybridFileStorage)

`IHybridFileStorage` - interface designed for hybrid file storage systems that facilitates the management of file operations, ensuring reliable and resilient access to files across multiple storage providers.

- Supports file operations such as uploading, downloading, and deleting files.
- Integrates multiple storage providers (e.g., file system, s3, PostgreSQL) for enhanced reliability.
- Automatically switches between providers in case one becomes unavailable, ensuring continuous access to files.
- Promotes improved resilience and availability of file data in applications requiring dependable storage management.

## [Sa.Configuration](src/Sa.Configuration)

- `Arguments` class parses command-line arguments in a C# application, enabling easy retrieval of parameter values through a dictionary-like interface. It supports both single-value and multi-value parameters for flexible command-line configurations.
- `Secrets` class securely manages sensitive information, such as API keys and database passwords, from various sources. It can load secrets from files, environment variables, and dynamically generated host key files.

## [Sa.Configuration.PostgreSql](src/Sa.Configuration.PostgreSql)

`AddPostgreSqlConfiguration` extension method allows you to add a PostgreSQL-based configuration source to an IConfigurationBuilder.

- This setup allows for dynamic configuration management, where changes in the database can be reflected in the application without needing to recompile or redeploy.

## [Sa.Media](src/Sa.Media)

- `AsyncWavReader` async and memory-efficient WAV file reader for .NET

## [Sa.Media.FFmpeg](src/Sa.Media.FFmpeg)

FFmpeg .NET Wrapper - ready to use out of the box with minimal setup

- Extract metadata from media files (duration, channels, sample rate, etc.)
- Convert audio to: WAV, MP3, MP4, OGG ..
- Splits/Join audio file by channels
- Built-in FFmpeg binaries for Windows x64 and Linux
- Supports Dependency Injection (DI) via standard IServiceCollection integration
