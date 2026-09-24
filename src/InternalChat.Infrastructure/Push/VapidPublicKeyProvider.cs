using InternalChat.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace InternalChat.Infrastructure.Push;

/// <summary>Reads the public half of <see cref="VapidOptions"/> for <see cref="IVapidPublicKeyProvider"/>.</summary>
public sealed class VapidPublicKeyProvider : IVapidPublicKeyProvider
{
    private readonly IOptions<VapidOptions> _options;

    /// <summary>Creates the provider.</summary>
    public VapidPublicKeyProvider(IOptions<VapidOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public string PublicKey => _options.Value.PublicKey;
}
