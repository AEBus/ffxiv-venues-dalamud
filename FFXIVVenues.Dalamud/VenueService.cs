using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace FFXIVVenues.Dalamud;

public sealed class VenueService : IVenueService, IDisposable
{
    private static readonly byte[] FallbackLoadingImage = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/w8AAgMBAp+N7WAAAAAASUVORK5CYII=");

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly HttpClient _httpClient;
    private readonly ITextureProvider _textureProvider;
    private readonly Dictionary<string, IDalamudTextureWrap?> _banners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _bannerTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDalamudTextureWrap _loadingTexture;
    private readonly object _bannerLock = new();
    private bool _disposed;

    public VenueService(IDalamudPluginInterface pluginInterface, HttpClient httpClient, ITextureProvider textureProvider)
    {
        _pluginInterface = pluginInterface;
        _httpClient = httpClient;
        _textureProvider = textureProvider;
        _loadingTexture = LoadLoadingTexture();
    }

    public IDalamudTextureWrap? GetVenueBanner(string venueId, Uri? bannerUri)
    {
        var requestUri = bannerUri?.ToString() ?? $"venue/{venueId}/media";
        lock (_bannerLock)
        {
            if (_banners.TryGetValue(requestUri, out var banner))
            {
                return banner;
            }

            if (!_bannerTasks.ContainsKey(requestUri))
            {
                _bannerTasks[requestUri] = FetchBannerAsync(requestUri);
            }
        }

        return _loadingTexture;
    }

    private async Task FetchBannerAsync(string requestUri)
    {
        try
        {
            using var response = await _httpClient.GetAsync(requestUri).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                lock (_bannerLock)
                {
                    _banners[requestUri] = null;
                }

                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var texture = await _textureProvider
                .CreateFromImageAsync(bytes, $"FFXIVVenues.Banner.{requestUri}")
                .ConfigureAwait(false);

            lock (_bannerLock)
            {
                if (_banners.TryGetValue(requestUri, out var existing))
                {
                    existing?.Dispose();
                }

                _banners[requestUri] = texture;
            }
        }
        catch
        {
            lock (_bannerLock)
            {
                _banners[requestUri] = null;
            }
        }
        finally
        {
            lock (_bannerLock)
            {
                _bannerTasks.Remove(requestUri);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_bannerLock)
        {
            foreach (var banner in _banners.Values)
            {
                banner?.Dispose();
            }

            _banners.Clear();
            _bannerTasks.Clear();
        }

        _loadingTexture.Dispose();
    }

    private IDalamudTextureWrap LoadLoadingTexture()
    {
        var assemblyDirectory = _pluginInterface.AssemblyLocation.Directory?.FullName;
        var candidatePaths = assemblyDirectory == null
            ? Array.Empty<string>()
            : new[]
            {
                Path.Combine(assemblyDirectory, "Assets", "loading.png"),
                Path.Combine(assemblyDirectory, "loading.png"),
            };

        foreach (var path in candidatePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            return CreateTexture(bytes, $"FFXIVVenues.Loading.{Path.GetFileName(path)}");
        }

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith("loading.png", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                continue;
            }

            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            return CreateTexture(memoryStream.ToArray(), "FFXIVVenues.Loading.Embedded");
        }

        return CreateTexture(FallbackLoadingImage, "FFXIVVenues.Loading.Fallback");
    }

    private IDalamudTextureWrap CreateTexture(byte[] bytes, string debugName) =>
        _textureProvider.CreateFromImageAsync(bytes, debugName).GetAwaiter().GetResult();
}

public interface IVenueService
{
    IDalamudTextureWrap? GetVenueBanner(string venueId, Uri? bannerUri);
}
