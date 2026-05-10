using System.Text;
using System.Text.Json;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Management.Application.Policy;

public sealed class ManagementPolicyValidationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IManagementPolicyModelYamlSerializer _yamlSerializer;

    public ManagementPolicyValidationService(IManagementPolicyModelYamlSerializer yamlSerializer)
    {
        _yamlSerializer = yamlSerializer ?? throw new ArgumentNullException(nameof(yamlSerializer));
    }

    public async Task<ManagementPolicyValidationResponse> ValidateAsync(
        ManagementPolicyValidationRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await ValidateProposalAsync(request, cancellationToken).ConfigureAwait(false);
        return outcome.Response;
    }

    public async Task<ManagementPolicyValidationOutcome> ValidateProposalAsync(
        ManagementPolicyValidationRequest request,
        CancellationToken cancellationToken)
    {
        var proposal = await ReadProposedPolicyAsync(request, cancellationToken).ConfigureAwait(false);
        var yaml = proposal.Yaml;

        var errors = proposal.Errors.ToList();
        try
        {
            var policy = ProxyWardPolicyLoader.Load(yaml);
            if (errors.Count > 0)
            {
                return new ManagementPolicyValidationOutcome(
                    yaml,
                    Policy: null,
                    Response: ManagementPolicyValidationResponse.Invalid(errors),
                    proposal.RequestedBy,
                    proposal.Note);
            }

            return new ManagementPolicyValidationOutcome(
                yaml,
                policy,
                ManagementPolicyValidationResponse.Success(
                    policy.VersionHash,
                    ManagementPolicyReader.CreateModel(policy)),
                proposal.RequestedBy,
                proposal.Note);
        }
        catch (PolicyValidationException ex)
        {
            errors.AddRange(ex.Errors.Select(MapPolicyError));
            return new ManagementPolicyValidationOutcome(
                yaml,
                Policy: null,
                Response: ManagementPolicyValidationResponse.Invalid(errors),
                proposal.RequestedBy,
                proposal.Note);
        }
    }

    private async Task<ProposedPolicyInput> ReadProposedPolicyAsync(
        ManagementPolicyValidationRequest request,
        CancellationToken cancellationToken)
    {
        if (IsRawYamlContentType(request.ContentType))
        {
            var rawYaml = await ReadBodyAsStringAsync(request.Body, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(rawYaml))
            {
                throw new ManagementPolicyValidationRequestException("Request body must not be empty.");
            }

            return new ProposedPolicyInput(rawYaml, [], RequestedBy: null, Note: null);
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ManagementPolicyValidationRequestException($"Request JSON could not be parsed: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ManagementPolicyValidationRequestException("Request body must be a JSON object.");
            }

            var hasYaml = document.RootElement.TryGetProperty("yaml", out var yamlElement)
                && yamlElement.ValueKind != JsonValueKind.Null;
            var hasModel = document.RootElement.TryGetProperty("model", out var modelElement)
                && modelElement.ValueKind != JsonValueKind.Null;

            if (hasYaml == hasModel)
            {
                throw new ManagementPolicyValidationRequestException("Request must provide either yaml or model.");
            }

            var requestedBy = ReadOptionalString(document.RootElement, "requestedBy");
            var note = ReadOptionalString(document.RootElement, "note");

            if (hasYaml)
            {
                if (yamlElement.ValueKind != JsonValueKind.String)
                {
                    throw new ManagementPolicyValidationRequestException("yaml must be a string.");
                }

                var yaml = yamlElement.GetString();
                if (string.IsNullOrWhiteSpace(yaml))
                {
                    throw new ManagementPolicyValidationRequestException("yaml must not be empty.");
                }

                return new ProposedPolicyInput(yaml, [], requestedBy, note);
            }

            if (modelElement.ValueKind != JsonValueKind.Object)
            {
                throw new ManagementPolicyValidationRequestException("model must be an object.");
            }

            ManagementPolicyModel? model;
            try
            {
                model = modelElement.Deserialize<ManagementPolicyModel>(JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new ManagementPolicyValidationRequestException($"model could not be parsed: {ex.Message}");
            }

            if (model is null)
            {
                throw new ManagementPolicyValidationRequestException("model must be an object.");
            }

            return new ProposedPolicyInput(
                _yamlSerializer.ToYaml(model),
                [],
                requestedBy,
                note);
        }
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new ManagementPolicyValidationRequestException($"{propertyName} must be a string.");
        }

        return property.GetString();
    }

    private static async Task<string> ReadBodyAsStringAsync(
        Stream body,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsRawYamlContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "application/x-yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "text/yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "text/plain", StringComparison.OrdinalIgnoreCase);
    }

    private static ManagementPolicyValidationError MapPolicyError(string message) =>
        new(
            Field: InferField(message),
            Code: "policy_validation_error",
            Message: message);

    private static string InferField(string message)
    {
        if (message.StartsWith("YAML could not be parsed:", StringComparison.OrdinalIgnoreCase))
        {
            return "yaml";
        }

        if (string.Equals(message, ProxyWardPolicyLoader.RemovedLockfileMessage, StringComparison.Ordinal))
        {
            return "lockfile";
        }

        foreach (var separator in new[] { " must ", " is ", " cannot ", " contains " })
        {
            var index = message.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                var field = message[..index].Trim();
                return field.EndsWith(" section", StringComparison.Ordinal)
                    ? field[..^" section".Length]
                    : field;
            }
        }

        return "policy";
    }

    private sealed record ProposedPolicyInput(
        string Yaml,
        IReadOnlyList<ManagementPolicyValidationError> Errors,
        string? RequestedBy,
        string? Note);
}

public sealed class ManagementPolicyValidationRequestException : Exception
{
    public ManagementPolicyValidationRequestException(string message)
        : base(message)
    {
    }
}

public sealed record ManagementPolicyValidationRequest(
    Stream Body,
    string? ContentType);

public sealed record ManagementPolicyValidationResponse(
    bool Valid,
    IReadOnlyList<ManagementPolicyValidationError> Errors,
    IReadOnlyList<ManagementPolicyValidationWarning> Warnings,
    string? PolicyHash,
    ManagementPolicyModel? NormalizedModel)
{
    public static ManagementPolicyValidationResponse Success(
        string policyHash,
        ManagementPolicyModel normalizedModel) =>
        new(
            Valid: true,
            Errors: [],
            Warnings: [],
            PolicyHash: policyHash,
            NormalizedModel: normalizedModel);

    public static ManagementPolicyValidationResponse Invalid(
        IReadOnlyList<ManagementPolicyValidationError> errors) =>
        new(
            Valid: false,
            Errors: errors,
            Warnings: [],
            PolicyHash: null,
            NormalizedModel: null);
}

public sealed record ManagementPolicyValidationError(
    string Field,
    string Code,
    string Message);

public sealed record ManagementPolicyValidationWarning(
    string Field,
    string Code,
    string Message);

public sealed record ManagementPolicyValidationOutcome(
    string Yaml,
    ProxyWardPolicy? Policy,
    ManagementPolicyValidationResponse Response,
    string? RequestedBy,
    string? Note);
