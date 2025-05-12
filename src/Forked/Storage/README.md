# Клиент для S3

## Forked

- https://github.com/dundich/Storage;
- https://github.com/teoadal/Storage;


Это обертка над HttpClient для работы с S3 хранилищами. Мотивация создания была простейшей - я не понимал,
почему клиенты [AWS](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/welcome.html) (4.0.0)
и [Minio](https://github.com/minio/minio-dotnet) (6.0.4) потребляют так много памяти. Результат экспериментов: скорость
почти как у AWS, а потребление памяти почти в 150 раз меньше, чем клиент для Minio (и в 17 для AWS).
