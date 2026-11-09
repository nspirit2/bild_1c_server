using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Deliberately offline: no SQL connection, key-ring directory, or external services.
public sealed class TargetCredentialProtectionTests
{
    private const string Unavailable = "Target credentials unavailable.";
    private const string ValidJson = "{\"clusterUser\":\"cluster-user\",\"clusterPassword\":\"cluster-password\",\"sqlUser\":\"sql-user\",\"sqlPassword\":\"sql-password\"}";
    private static readonly string[] Keys = ["clusterUser", "clusterPassword", "sqlUser", "sqlPassword"];

    private static TargetCredentialScope Scope() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static TargetCredentialSecrets Secrets() => new("cluster-user-secret", "cluster-password-secret", "sql-user-secret", "sql-password-secret");
    private static DataProtectionTargetCredentialProtector Real() => new(new EphemeralDataProtectionProvider());

    private static TargetCredentialScope ReplaceScope(TargetCredentialScope scope, int field, Guid value) => field switch
    {
        0 => scope with { CredentialId = value },
        1 => scope with { CanonicalClusterId = value },
        2 => scope with { CanonicalSqlServerId = value },
        _ => scope with { EndpointMappingId = value }
    };

    private static TargetCredentialSecrets ReplaceSecret(TargetCredentialSecrets secrets, int field, string value) => field switch
    {
        0 => secrets with { ClusterUser = value },
        1 => secrets with { ClusterPassword = value },
        2 => secrets with { SqlUser = value },
        _ => secrets with { SqlPassword = value }
    };

    private static void UnavailableFailure(Action action, params string[] sensitive)
    {
        var error = Assert.Throws<TargetCredentialProtectionException>(action);
        Assert.Equal(Unavailable, error.Message);
        Assert.Equal(Unavailable, error.ToString());
        Assert.Null(error.InnerException);
        foreach (var value in sensitive)
            Assert.DoesNotContain(value, error.ToString(), StringComparison.Ordinal);
    }

    private static void RejectPlaintext(byte[] plaintext)
    {
        var provider = new ControlledProvider();
        var codec = new DataProtectionTargetCredentialProtector(provider);
        var scope = Scope();
        var ciphertext = codec.Protect(scope, Secrets());
        provider.UnprotectedBytes = plaintext;
        UnavailableFailure(() => codec.Unprotect(scope, ciphertext), "cluster-password", "sql-password");
    }

    private static void RejectJson(string json) => RejectPlaintext(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void All_four_credentials_round_trip()
    {
        var codec = Real(); var scope = Scope(); var secrets = Secrets();
        Assert.Equal(secrets, codec.Unprotect(scope, codec.Protect(scope, secrets)));
    }

    [Fact]
    public void Ciphertext_does_not_contain_any_raw_credential()
    {
        var ciphertext = Real().Protect(Scope(), Secrets());
        foreach (var value in new[] { Secrets().ClusterUser, Secrets().ClusterPassword, Secrets().SqlUser, Secrets().SqlPassword })
            Assert.DoesNotContain(value, ciphertext, StringComparison.Ordinal);
    }

    [Fact]
    public void Unicode_quotes_and_backslashes_round_trip_without_normalization()
    {
        var codec = Real(); var scope = Scope();
        var secrets = new TargetCredentialSecrets("имя😀", "p\"a\\ss", "e\u0301-user", " 密碼 ");
        Assert.Equal(secrets, codec.Unprotect(scope, codec.Protect(scope, secrets)));
    }

    [Fact]
    public void Maximum_valid_field_lengths_round_trip()
    {
        var codec = Real(); var scope = Scope(); var value = new string('x', 4096);
        var secrets = new TargetCredentialSecrets(value, value, value, value);
        Assert.Equal(secrets, codec.Unprotect(scope, codec.Protect(scope, secrets)));
    }

    private static void RejectDifferentScope(int field)
    {
        var codec = Real(); var scope = Scope(); var ciphertext = codec.Protect(scope, Secrets());
        UnavailableFailure(() => codec.Unprotect(ReplaceScope(scope, field, Guid.NewGuid()), ciphertext));
    }

    [Fact] public void Credential_id_is_bound_to_ciphertext() => RejectDifferentScope(0);
    [Fact] public void Canonical_cluster_id_is_bound_to_ciphertext() => RejectDifferentScope(1);
    [Fact] public void Canonical_sql_server_id_is_bound_to_ciphertext() => RejectDifferentScope(2);
    [Fact] public void Endpoint_mapping_id_is_bound_to_ciphertext() => RejectDifferentScope(3);

    [Fact]
    public void Empty_guid_in_each_scope_position_is_rejected_in_both_directions()
    {
        var codec = Real(); var scope = Scope(); var ciphertext = codec.Protect(scope, Secrets());
        for (var field = 0; field < 4; field++)
        {
            var invalid = ReplaceScope(scope, field, Guid.Empty);
            UnavailableFailure(() => codec.Protect(invalid, Secrets()));
            UnavailableFailure(() => codec.Unprotect(invalid, ciphertext));
        }
    }

    [Fact]
    public void Each_field_rejects_null_empty_oversize_and_control_characters()
    {
        var codec = Real();
        foreach (var invalid in new[] { null!, "", new string('x', 4097), "a\0b", "a\nb", "a\rb", "a\tb", "a\u007fb", "a\u0085b" })
            for (var field = 0; field < 4; field++)
            {
                var secrets = ReplaceSecret(Secrets(), field, invalid);
                UnavailableFailure(() => codec.Protect(Scope(), secrets));
            }
    }

    [Fact]
    public void Different_provider_cannot_unprotect_ciphertext()
    {
        var scope = Scope(); var ciphertext = Real().Protect(scope, Secrets());
        UnavailableFailure(() => Real().Unprotect(scope, ciphertext));
    }

    [Fact]
    public void Modified_ciphertext_is_rejected()
    {
        var codec = Real(); var scope = Scope(); var ciphertext = codec.Protect(scope, Secrets());
        var index = ciphertext.Length / 2;
        var changed = ciphertext[..index] + (ciphertext[index] == 'A' ? "B" : "A") + ciphertext[(index + 1)..];
        UnavailableFailure(() => codec.Unprotect(scope, changed));
    }

    [Fact]
    public void Null_empty_and_invalid_base64_ciphertext_are_rejected()
    {
        var codec = Real(); var scope = Scope();
        foreach (var invalid in new[] { null!, "", " ", "%%%", "not ciphertext!", "A", "====" })
            UnavailableFailure(() => codec.Unprotect(scope, invalid));
    }

    [Fact]
    public void Ciphertext_over_256_Ki_characters_is_rejected_before_decryption()
    {
        var provider = new ControlledProvider(); var codec = new DataProtectionTargetCredentialProtector(provider);
        UnavailableFailure(() => codec.Unprotect(Scope(), new string('A', 256 * 1024 + 1)));
        Assert.Equal(0, provider.UnprotectCalls);
    }

    [Fact]
    public void Protection_failure_does_not_expose_crypto_exception_or_credentials()
    {
        var provider = new ControlledProvider { FailProtect = true };
        var codec = new DataProtectionTargetCredentialProtector(provider);
        UnavailableFailure(() => codec.Protect(Scope(), Secrets()), ControlledProvider.SensitiveFailure, Secrets().ClusterPassword, Secrets().SqlPassword);
    }

    [Fact]
    public void Unprotection_failure_does_not_expose_crypto_exception_or_credentials()
    {
        var provider = new ControlledProvider(); var codec = new DataProtectionTargetCredentialProtector(provider); var scope = Scope();
        var ciphertext = codec.Protect(scope, Secrets()); provider.FailUnprotect = true;
        UnavailableFailure(() => codec.Unprotect(scope, ciphertext), ControlledProvider.SensitiveFailure, Secrets().ClusterPassword, Secrets().SqlPassword);
    }

    [Fact]
    public void Secrets_string_representation_is_redacted()
    {
        var secrets = Secrets();
        foreach (var value in new[] { secrets.ClusterUser, secrets.ClusterPassword, secrets.SqlUser, secrets.SqlPassword })
            Assert.DoesNotContain(value, secrets.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Scope_string_representation_does_not_expose_binding_identifiers()
    {
        var scope = Scope();
        foreach (var id in new[] { scope.CredentialId, scope.CanonicalClusterId, scope.CanonicalSqlServerId, scope.EndpointMappingId })
            Assert.DoesNotContain(id.ToString("D"), scope.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Purpose_chain_contains_all_scope_ids_and_a_domain_version_prefix()
    {
        var provider = new ControlledProvider(); var scope = Scope();
        new DataProtectionTargetCredentialProtector(provider).Protect(scope, Secrets());
        var purposes = string.Join("|", provider.Purposes);
        foreach (var id in new[] { scope.CredentialId, scope.CanonicalClusterId, scope.CanonicalSqlServerId, scope.EndpointMappingId })
            Assert.Contains(id.ToString("D"), purposes, StringComparison.OrdinalIgnoreCase);
        Assert.True(provider.Purposes.Any(p => !Guid.TryParse(p, out _)), "A non-GUID domain/version purpose is required.");
    }

    [Fact]
    public void Serialized_plaintext_has_exactly_four_camel_case_string_properties()
    {
        var provider = new ControlledProvider();
        new DataProtectionTargetCredentialProtector(provider).Protect(Scope(), Secrets());
        using var document = JsonDocument.Parse(provider.ProtectedBytes!);
        var properties = document.RootElement.EnumerateObject().ToArray();
        Assert.Equal(Keys.OrderBy(k => k), properties.Select(p => p.Name).OrderBy(k => k));
        Assert.All(properties, property => Assert.Equal(JsonValueKind.String, property.Value.ValueKind));
    }

    [Fact]
    public void Strict_parser_rejects_non_object_roots_and_malformed_documents()
    {
        foreach (var json in new[] { "", "null", "[]", "\"value\"", "42", "{", ValidJson[..^1] + ",}", "/*comment*/" + ValidJson })
            RejectJson(json);
    }

    [Fact]
    public void Strict_parser_rejects_duplicate_keys_including_escaped_equivalents()
    {
        RejectJson(ValidJson[..^1] + ",\"clusterUser\":\"replacement\"}");
        RejectJson(ValidJson[..^1] + ",\"cluster\\u0055ser\":\"replacement\"}");
    }

    [Fact]
    public void Strict_parser_rejects_unknown_and_wrong_case_keys()
    {
        RejectJson(ValidJson[..^1] + ",\"extra\":\"value\"}");
        RejectJson(ValidJson.Replace("clusterUser", "ClusterUser", StringComparison.Ordinal));
    }

    [Fact]
    public void Strict_parser_rejects_missing_null_and_nonstring_values_for_each_field()
    {
        foreach (var key in Keys)
        {
            var entries = Keys.Where(k => k != key).Select(k => $"\"{k}\":\"valid\"");
            RejectJson("{" + string.Join(",", entries) + "}");
            foreach (var value in new[] { "null", "true", "1", "[]", "{}" })
                RejectJson("{" + string.Join(",", Keys.Select(k => $"\"{k}\":" + (k == key ? value : "\"valid\""))) + "}");
        }
    }

    [Fact]
    public void Strict_parser_validates_all_decrypted_credential_values()
    {
        foreach (var key in Keys)
            foreach (var invalid in new[] { "", new string('x', 4097), "a\0b", "a\nb", "a\u007fb" })
            {
                var values = Keys.ToDictionary(k => k, k => k == key ? invalid : "valid");
                RejectJson(JsonSerializer.Serialize(values));
            }
    }

    [Fact]
    public void Strict_parser_rejects_trailing_documents_and_trailing_nonwhitespace()
    {
        foreach (var suffix in new[] { "{}", "null", "x", "\0" }) RejectJson(ValidJson + suffix);
    }

    [Fact]
    public void Strict_parser_rejects_invalid_utf8_instead_of_replacing_it()
    {
        var prefix = Encoding.UTF8.GetBytes(ValidJson.Replace("cluster-user", "", StringComparison.Ordinal));
        var insertion = Encoding.UTF8.GetByteCount("{\"clusterUser\":\"");
        foreach (var invalid in new[] { new byte[] { 0xff }, new byte[] { 0xc0, 0xaf }, new byte[] { 0xed, 0xa0, 0x80 }, new byte[] { 0xc3 } })
            RejectPlaintext([.. prefix[..insertion], .. invalid, .. prefix[insertion..]]);
    }

    [Fact]
    public void Plaintext_over_128_Ki_bytes_is_rejected_even_when_json_is_otherwise_valid()
    {
        RejectPlaintext(Encoding.UTF8.GetBytes(ValidJson + new string(' ', 128 * 1024)));
    }

    private sealed class ControlledProvider : IDataProtectionProvider, IDataProtector
    {
        public const string SensitiveFailure = "raw-crypto-failure-cluster-password-secret-sql-password-secret";
        public List<string> Purposes { get; } = [];
        public byte[]? ProtectedBytes { get; private set; }
        public byte[]? UnprotectedBytes { get; set; }
        public bool FailProtect { get; set; }
        public bool FailUnprotect { get; set; }
        public int UnprotectCalls { get; private set; }

        public IDataProtector CreateProtector(string purpose) { Purposes.Add(purpose); return this; }
        public byte[] Protect(byte[] plaintext)
        {
            if (FailProtect) throw new CryptographicException(SensitiveFailure);
            ProtectedBytes = plaintext.ToArray();
            return [1, 2, 3, 4, 5, 6, 7, 8];
        }
        public byte[] Unprotect(byte[] protectedData)
        {
            UnprotectCalls++;
            if (FailUnprotect) throw new CryptographicException(SensitiveFailure);
            return UnprotectedBytes ?? ProtectedBytes ?? throw new CryptographicException(SensitiveFailure);
        }
    }
}
