using ReleaseGuard.WebhookIngestion.Api;
using Microsoft.Extensions.Options;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<GitHubWebhookOptions>()
    .BindConfiguration(GitHubWebhookOptions.SectionName)
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.Secret),
        $"{GitHubWebhookOptions.SectionName}:Secret must be configured.")
    .Validate(
        options => options.Secret is { Length: >= GitHubWebhookOptions.MinimumSecretLength },
        $"{GitHubWebhookOptions.SectionName}:Secret must contain at least {GitHubWebhookOptions.MinimumSecretLength} characters.")
    .ValidateOnStart();

builder.Services
    .AddOptions<PostgreSqlOptions>()
    .BindConfiguration(PostgreSqlOptions.SectionName)
    .Validate(
        PostgreSqlOptions.HasValidConnectionString,
        $"{PostgreSqlOptions.SectionName}:ConnectionString must be a valid PostgreSQL connection string with Host and Database.")
    .ValidateOnStart();

builder.Services
    .AddOptions<KafkaProducerOptions>()
    .BindConfiguration(KafkaProducerOptions.SectionName)
    .Validate(
        KafkaProducerOptions.HasValidBootstrapServers,
        $"{KafkaProducerOptions.SectionName}:BootstrapServers must contain one or more host:port endpoints.")
    .Validate(
        KafkaProducerOptions.HasValidTopic,
        $"{KafkaProducerOptions.SectionName}:Topic must be a valid explicit Kafka topic name of at most 249 UTF-8 bytes.")
    .Validate(
        KafkaProducerOptions.HasValidClientId,
        $"{KafkaProducerOptions.SectionName}:ClientId must contain between 1 and 128 characters.")
    .Validate(
        KafkaProducerOptions.HasValidTimeouts,
        $"{KafkaProducerOptions.SectionName} timeouts must be between {KafkaProducerOptions.MinimumTimeoutMilliseconds} and {KafkaProducerOptions.MaximumTimeoutMilliseconds} milliseconds, and RequestTimeoutMilliseconds must not exceed DeliveryTimeoutMilliseconds.")
    .Validate(
        KafkaProducerOptions.HasValidRetryLimit,
        $"{KafkaProducerOptions.SectionName}:MaximumRetries must be between {KafkaProducerOptions.MinimumRetries} and {KafkaProducerOptions.MaximumAllowedRetries}.")
    .ValidateOnStart();

builder.Services
    .AddOptions<KafkaConsumerOptions>()
    .BindConfiguration(KafkaConsumerOptions.SectionName)
    .Validate(
        KafkaConsumerOptions.HasValidBootstrapServers,
        $"{KafkaConsumerOptions.SectionName}:BootstrapServers must contain one or more host:port endpoints.")
    .Validate(
        KafkaConsumerOptions.HasValidTopic,
        $"{KafkaConsumerOptions.SectionName}:Topic must be a valid explicit Kafka topic name of at most 249 UTF-8 bytes.")
    .Validate(
        KafkaConsumerOptions.HasValidGroupId,
        $"{KafkaConsumerOptions.SectionName}:GroupId must be a printable value of at most {KafkaConsumerOptions.MaximumGroupIdUtf8Bytes} UTF-8 bytes.")
    .Validate(
        KafkaConsumerOptions.HasValidClientId,
        $"{KafkaConsumerOptions.SectionName}:ClientId must be a printable value of at most 128 UTF-8 bytes.")
    .Validate(
        KafkaConsumerOptions.HasValidConsumeTimeout,
        $"{KafkaConsumerOptions.SectionName}:ConsumeTimeoutMilliseconds must be between {KafkaConsumerOptions.MinimumConsumeTimeoutMilliseconds} and {KafkaConsumerOptions.MaximumConsumeTimeoutMilliseconds}.")
    .Validate(
        KafkaConsumerOptions.HasValidBrokerRequestTimeout,
        $"{KafkaConsumerOptions.SectionName}:BrokerRequestTimeoutMilliseconds must be between {KafkaConsumerOptions.MinimumBrokerRequestTimeoutMilliseconds} and {KafkaConsumerOptions.MaximumBrokerRequestTimeoutMilliseconds}.")
    .ValidateOnStart();
builder.Services.AddSingleton<
    IValidateOptions<KafkaConsumerOptions>,
    KafkaConsumerOptionsValidator>();

builder.Services
    .AddOptions<OutboxDispatcherOptions>()
    .BindConfiguration(OutboxDispatcherOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<
    IValidateOptions<OutboxDispatcherOptions>,
    OutboxDispatcherOptionsValidator>();

builder.Services
    .AddOptions<ReleaseRiskInboxProcessorOptions>()
    .BindConfiguration(ReleaseRiskInboxProcessorOptions.SectionName)
    .Validate(
        ReleaseRiskInboxProcessorOptions.IsValid,
        $"{ReleaseRiskInboxProcessorOptions.SectionName}:PersistenceTimeoutMilliseconds must be between {ReleaseRiskInboxProcessorOptions.MinimumPersistenceTimeoutMilliseconds} and {ReleaseRiskInboxProcessorOptions.MaximumPersistenceTimeoutMilliseconds}.")
    .ValidateOnStart();

builder.Services
    .AddOptions<AiExplanationClientOptions>()
    .BindConfiguration(AiExplanationClientOptions.SectionName)
    .Validate(
        AiExplanationClientOptions.HasValidBaseUrl,
        $"{AiExplanationClientOptions.SectionName}:BaseUrl must be an absolute HTTP or HTTPS URL without credentials, query, or fragment.")
    .Validate(
        AiExplanationClientOptions.HasValidRequestTimeout,
        $"{AiExplanationClientOptions.SectionName}:RequestTimeoutMilliseconds must be between {AiExplanationClientOptions.MinimumRequestTimeoutMilliseconds} and {AiExplanationClientOptions.MaximumRequestTimeoutMilliseconds}.")
    .ValidateOnStart();

builder.Services
    .AddOptions<AiExplanationProcessorOptions>()
    .BindConfiguration(AiExplanationProcessorOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<
    IValidateOptions<AiExplanationProcessorOptions>,
    AiExplanationProcessorOptionsValidator>();

builder.Services
    .AddOptions<AiExplanationQueryOptions>()
    .BindConfiguration(AiExplanationQueryOptions.SectionName)
    .Validate(
        AiExplanationQueryOptions.IsValid,
        $"{AiExplanationQueryOptions.SectionName}:ReadTimeoutMilliseconds must be between {AiExplanationQueryOptions.MinimumReadTimeoutMilliseconds} and {AiExplanationQueryOptions.MaximumReadTimeoutMilliseconds}.")
    .ValidateOnStart();

builder.Services
    .AddOptions<AiExplanationQueryAuthenticationOptions>()
    .BindConfiguration(AiExplanationQueryAuthenticationOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<
    IValidateOptions<AiExplanationQueryAuthenticationOptions>,
    AiExplanationQueryAuthenticationOptionsValidator>();

builder.Services.AddSingleton<GitHubWebhookSignatureValidator>();
builder.Services.AddSingleton<AiExplanationQueryAuthenticator>();
builder.Services.AddSingleton<GitHubRiskInputMapper>();
builder.Services.AddSingleton<ReleaseRiskEvaluator>();
builder.Services.AddHttpClient<
    IReleaseRiskExplanationClient,
    HttpReleaseRiskExplanationClient>();
builder.Services.AddSingleton<
    IReleaseRiskEventProducer,
    KafkaReleaseRiskEventProducer>();
builder.Services.AddSingleton<
    IReleaseRiskEventConsumer,
    KafkaReleaseRiskEventConsumer>();
builder.Services.AddSingleton<NpgsqlDataSource>(serviceProvider =>
{
    var options = serviceProvider
        .GetRequiredService<IOptions<PostgreSqlOptions>>()
        .Value;
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(options.ConnectionString);
    dataSourceBuilder.UseLoggerFactory(
        serviceProvider.GetRequiredService<ILoggerFactory>());
    return dataSourceBuilder.Build();
});
builder.Services.AddSingleton<
    IGitHubWebhookDeliveryStore,
    PostgreSqlGitHubWebhookDeliveryStore>();
builder.Services.AddSingleton<
    IReleaseRiskOutboxStore,
    PostgreSqlReleaseRiskOutboxStore>();
builder.Services.AddSingleton<
    IReleaseRiskInboxStore,
    PostgreSqlReleaseRiskInboxStore>();
builder.Services.AddSingleton<
    IReleaseRiskExplanationStore,
    PostgreSqlReleaseRiskExplanationStore>();
builder.Services.AddSingleton<
    IReleaseRiskExplanationQuery,
    PostgreSqlReleaseRiskExplanationQuery>();
builder.Services.AddHostedService<PostgreSqlSchemaInitializer>();
builder.Services.AddHostedService<ReleaseRiskOutboxDispatcher>();
builder.Services.AddSingleton<ReleaseRiskInboxProcessor>(serviceProvider =>
    new ReleaseRiskInboxProcessor(
        () => serviceProvider.GetRequiredService<IReleaseRiskEventConsumer>(),
        serviceProvider.GetRequiredService<IReleaseRiskInboxStore>(),
        serviceProvider.GetRequiredService<
            IOptions<ReleaseRiskInboxProcessorOptions>>(),
        serviceProvider.GetRequiredService<
            ILogger<ReleaseRiskInboxProcessor>>()));
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<ReleaseRiskInboxProcessor>());
builder.Services.AddHostedService<ReleaseRiskExplanationProcessor>();

var app = builder.Build();

app.MapGet("/health", () => TypedResults.Ok(ServiceStatus.Ready()));
app.MapPost(GitHubWebhookEndpoint.Route, GitHubWebhookEndpoint.HandleAsync);
app.MapGet(
    ReleaseRiskExplanationQueryEndpoint.Route,
    ReleaseRiskExplanationQueryEndpoint.HandleAsync);

app.Run();

public partial class Program;
