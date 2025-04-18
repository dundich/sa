# sa
dot net experimental & plans to create NuGet packages...

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
