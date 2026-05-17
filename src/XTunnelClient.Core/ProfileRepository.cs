using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace XTunnelClient.Core;

public interface ISecretStore
{
    void SaveSecret(Guid profileId, string secretRef, string value);
    string? GetSecret(Guid profileId, string secretRef);
    void DeleteSecrets(Guid profileId);
}

public sealed class ProfileRepository : ISecretStore
{
    private readonly AppPaths _paths;
    private readonly string _connectionString;

    public ProfileRepository(AppPaths paths)
    {
        _paths = paths;
        _paths.Ensure();
        SQLitePCL.Batteries_V2.Init();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.Database,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        EnsureCreated();
    }

    public void EnsureCreated()
    {
        using var connection = Open();
        Execute(connection, """
            create table if not exists profiles (
                id text primary key,
                name text not null,
                kind text not null,
                enabled integer not null,
                source text not null,
                created_at text not null,
                updated_at text not null,
                core_config_json text not null,
                secret_ref text null,
                color text null,
                sort_order integer not null,
                last_validated_at text null,
                last_validation_error text null,
                last_endpoint_test_at text null,
                last_endpoint_test_duration_ms integer null,
                last_endpoint_test_error text null,
                last_endpoint_test_target text null
            );
            """);
        EnsureColumn(connection, "profiles", "last_endpoint_test_at", "text null");
        EnsureColumn(connection, "profiles", "last_endpoint_test_duration_ms", "integer null");
        EnsureColumn(connection, "profiles", "last_endpoint_test_error", "text null");
        EnsureColumn(connection, "profiles", "last_endpoint_test_target", "text null");
        Execute(connection, """
            create table if not exists settings (
                key text primary key,
                value text not null
            );
            """);
        Execute(connection, """
            create table if not exists subscriptions (
                id text primary key,
                display_name text not null,
                url text not null,
                etag text null,
                last_modified text null,
                update_interval_minutes integer not null,
                last_result text not null,
                trust_policy text not null,
                last_updated_at text null
            );
            """);
        Execute(connection, """
            create table if not exists secrets (
                profile_id text not null,
                secret_ref text not null,
                protected_value text not null,
                primary key (profile_id, secret_ref)
            );
            """);
    }

    public List<Profile> GetProfiles()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select * from profiles order by sort_order, name";
        using var reader = command.ExecuteReader();
        var profiles = new List<Profile>();
        while (reader.Read())
        {
            profiles.Add(new Profile
            {
                Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                Name = reader.GetString(reader.GetOrdinal("name")),
                Kind = reader.GetString(reader.GetOrdinal("kind")),
                Enabled = reader.GetInt32(reader.GetOrdinal("enabled")) != 0,
                Source = reader.GetString(reader.GetOrdinal("source")),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
                CoreConfigJson = reader.GetString(reader.GetOrdinal("core_config_json")),
                SecretRef = ReadNullableString(reader, "secret_ref"),
                Color = ReadNullableString(reader, "color"),
                SortOrder = reader.GetInt32(reader.GetOrdinal("sort_order")),
                LastValidatedAt = ReadNullableDate(reader, "last_validated_at"),
                LastValidationError = ReadNullableString(reader, "last_validation_error"),
                LastEndpointTestAt = ReadNullableDate(reader, "last_endpoint_test_at"),
                LastEndpointTestDurationMs = ReadNullableInt64(reader, "last_endpoint_test_duration_ms"),
                LastEndpointTestError = ReadNullableString(reader, "last_endpoint_test_error"),
                LastEndpointTestTarget = ReadNullableString(reader, "last_endpoint_test_target")
            });
        }
        return profiles;
    }

    public void SaveProfile(Profile profile)
    {
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            insert into profiles (
                id, name, kind, enabled, source, created_at, updated_at, core_config_json,
                secret_ref, color, sort_order, last_validated_at, last_validation_error,
                last_endpoint_test_at, last_endpoint_test_duration_ms, last_endpoint_test_error, last_endpoint_test_target
            ) values (
                $id, $name, $kind, $enabled, $source, $created_at, $updated_at, $core_config_json,
                $secret_ref, $color, $sort_order, $last_validated_at, $last_validation_error,
                $last_endpoint_test_at, $last_endpoint_test_duration_ms, $last_endpoint_test_error, $last_endpoint_test_target
            )
            on conflict(id) do update set
                name = excluded.name,
                kind = excluded.kind,
                enabled = excluded.enabled,
                source = excluded.source,
                updated_at = excluded.updated_at,
                core_config_json = excluded.core_config_json,
                secret_ref = excluded.secret_ref,
                color = excluded.color,
                sort_order = excluded.sort_order,
                last_validated_at = excluded.last_validated_at,
                last_validation_error = excluded.last_validation_error,
                last_endpoint_test_at = excluded.last_endpoint_test_at,
                last_endpoint_test_duration_ms = excluded.last_endpoint_test_duration_ms,
                last_endpoint_test_error = excluded.last_endpoint_test_error,
                last_endpoint_test_target = excluded.last_endpoint_test_target;
            """;
        Add(command, "$id", profile.Id.ToString());
        Add(command, "$name", profile.Name);
        Add(command, "$kind", profile.Kind);
        Add(command, "$enabled", profile.Enabled ? 1 : 0);
        Add(command, "$source", profile.Source);
        Add(command, "$created_at", profile.CreatedAt.ToString("O"));
        Add(command, "$updated_at", profile.UpdatedAt.ToString("O"));
        Add(command, "$core_config_json", profile.CoreConfigJson);
        Add(command, "$secret_ref", profile.SecretRef);
        Add(command, "$color", profile.Color);
        Add(command, "$sort_order", profile.SortOrder);
        Add(command, "$last_validated_at", profile.LastValidatedAt?.ToString("O"));
        Add(command, "$last_validation_error", profile.LastValidationError);
        Add(command, "$last_endpoint_test_at", profile.LastEndpointTestAt?.ToString("O"));
        Add(command, "$last_endpoint_test_duration_ms", profile.LastEndpointTestDurationMs);
        Add(command, "$last_endpoint_test_error", profile.LastEndpointTestError);
        Add(command, "$last_endpoint_test_target", profile.LastEndpointTestTarget);
        command.ExecuteNonQuery();
    }

    public void DeleteProfile(Guid profileId)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "delete from profiles where id = $id";
            Add(command, "$id", profileId.ToString());
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "delete from secrets where profile_id = $id";
            Add(command, "$id", profileId.ToString());
            command.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public AppSettings GetSettings()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select value from settings where key = 'app'";
        var value = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return new AppSettings();
        }
        return JsonSerializer.Deserialize<AppSettings>(value, JsonDefaults.Web) ?? new AppSettings();
    }

    public void SaveSettings(AppSettings settings)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            insert into settings(key, value) values ('app', $value)
            on conflict(key) do update set value = excluded.value;
            """;
        Add(command, "$value", JsonSerializer.Serialize(settings, JsonDefaults.Pretty));
        command.ExecuteNonQuery();
    }

    public List<Subscription> GetSubscriptions()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select * from subscriptions order by display_name";
        using var reader = command.ExecuteReader();
        var subscriptions = new List<Subscription>();
        while (reader.Read())
        {
            subscriptions.Add(new Subscription
            {
                Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
                Url = reader.GetString(reader.GetOrdinal("url")),
                ETag = ReadNullableString(reader, "etag"),
                LastModified = ReadNullableString(reader, "last_modified"),
                UpdateIntervalMinutes = reader.GetInt32(reader.GetOrdinal("update_interval_minutes")),
                LastResult = reader.GetString(reader.GetOrdinal("last_result")),
                TrustPolicy = reader.GetString(reader.GetOrdinal("trust_policy")),
                LastUpdatedAt = ReadNullableDate(reader, "last_updated_at")
            });
        }
        return subscriptions;
    }

    public void SaveSubscription(Subscription subscription)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            insert into subscriptions (
                id, display_name, url, etag, last_modified, update_interval_minutes,
                last_result, trust_policy, last_updated_at
            ) values (
                $id, $display_name, $url, $etag, $last_modified, $update_interval_minutes,
                $last_result, $trust_policy, $last_updated_at
            )
            on conflict(id) do update set
                display_name = excluded.display_name,
                url = excluded.url,
                etag = excluded.etag,
                last_modified = excluded.last_modified,
                update_interval_minutes = excluded.update_interval_minutes,
                last_result = excluded.last_result,
                trust_policy = excluded.trust_policy,
                last_updated_at = excluded.last_updated_at;
            """;
        Add(command, "$id", subscription.Id.ToString());
        Add(command, "$display_name", subscription.DisplayName);
        Add(command, "$url", subscription.Url);
        Add(command, "$etag", subscription.ETag);
        Add(command, "$last_modified", subscription.LastModified);
        Add(command, "$update_interval_minutes", subscription.UpdateIntervalMinutes);
        Add(command, "$last_result", subscription.LastResult);
        Add(command, "$trust_policy", subscription.TrustPolicy);
        Add(command, "$last_updated_at", subscription.LastUpdatedAt?.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void DeleteSubscription(Guid subscriptionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "delete from subscriptions where id = $id";
        Add(command, "$id", subscriptionId.ToString());
        command.ExecuteNonQuery();
    }

    public void SaveSecret(Guid profileId, string secretRef, string value)
    {
        var protectedValue = Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(value)));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            insert into secrets(profile_id, secret_ref, protected_value) values ($profile_id, $secret_ref, $protected_value)
            on conflict(profile_id, secret_ref) do update set protected_value = excluded.protected_value;
            """;
        Add(command, "$profile_id", profileId.ToString());
        Add(command, "$secret_ref", secretRef);
        Add(command, "$protected_value", protectedValue);
        command.ExecuteNonQuery();
    }

    public string? GetSecret(Guid profileId, string secretRef)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select protected_value from secrets where profile_id = $profile_id and secret_ref = $secret_ref";
        Add(command, "$profile_id", profileId.ToString());
        Add(command, "$secret_ref", secretRef);
        var protectedValue = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }
        return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(protectedValue)));
    }

    public void DeleteSecrets(Guid profileId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "delete from secrets where profile_id = $profile_id";
        Add(command, "$profile_id", profileId.ToString());
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"pragma table_info({table})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(reader.GetOrdinal("name")), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        Execute(connection, $"alter table {table} add column {column} {definition}");
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string? ReadNullableString(SqliteDataReader reader, string name)
    {
        var index = reader.GetOrdinal(name);
        return reader.IsDBNull(index) ? null : reader.GetString(index);
    }

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value) ? null : DateTimeOffset.Parse(value);
    }

    private static long? ReadNullableInt64(SqliteDataReader reader, string name)
    {
        var index = reader.GetOrdinal(name);
        return reader.IsDBNull(index) ? null : reader.GetInt64(index);
    }

    private static byte[] Protect(byte[] value)
    {
        if (OperatingSystem.IsWindows())
        {
            return ProtectedData.Protect(value, null, DataProtectionScope.CurrentUser);
        }
        return value;
    }

    private static byte[] Unprotect(byte[] value)
    {
        if (OperatingSystem.IsWindows())
        {
            return ProtectedData.Unprotect(value, null, DataProtectionScope.CurrentUser);
        }
        return value;
    }
}
