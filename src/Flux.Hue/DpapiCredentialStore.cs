using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Flux.Hue;

/// <summary>
/// The bridge keys, DPAPI-encrypted under the current Windows account, in a file
/// beside the app's other per-user state.
///
/// One implementation serves development and an installed build alike. The plan
/// originally split them - user secrets while developing, DPAPI once installed - and
/// that split does not survive the fact that <b>pairing writes the key at runtime</b>
/// from the phone: user secrets are a tool for values a developer types, not for
/// state an application saves. The property that mattered is kept either way, since
/// %APPDATA% is exactly as far outside the repository as the secrets folder is, and
/// a credential that never enters the working tree cannot be committed by accident.
///
/// What DPAPI buys, precisely: the blob is useless to another Windows account and on
/// another machine, so a copied profile or a backed-up folder leaks nothing. What it
/// does not buy: protection from code already running as this user. Nothing at this
/// tier does, and for keys that control lamps that is the right place to stop.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiCredentialStore: ICredentialStore {
  private const string FileName = "hue-credentials.dat";

  private readonly string _directory;
  private readonly ILogger<DpapiCredentialStore> _logger;
  private readonly object _gate = new();

  public DpapiCredentialStore(string directory, ILogger<DpapiCredentialStore> logger) {
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);
    ArgumentNullException.ThrowIfNull(logger);

    _directory = directory;
    _logger = logger;
  }

  private string Path => System.IO.Path.Combine(_directory, FileName);

  public HueCredentials? Load() {
    lock(_gate) {
      if(!File.Exists(Path)) {
        return null;
      }

      byte[]? plaintext = null;
      try {
        plaintext = ProtectedData.Unprotect(File.ReadAllBytes(Path), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<HueCredentials>(plaintext);
      } catch(CryptographicException e) {
        // Written by another Windows account, or by this one on another machine.
        // Reported rather than thrown: the caller's answer to "no credentials" is to
        // offer pairing, which is also the only cure for this.
        _logger.LogWarning(e, "Stored Hue credentials at {Path} could not be decrypted. Pair the bridge again.", Path);
        return null;
      } catch(JsonException e) {
        _logger.LogWarning(e, "Stored Hue credentials at {Path} are not readable. Pair the bridge again.", Path);
        return null;
      } finally {
        if(plaintext is not null) {
          CryptographicOperations.ZeroMemory(plaintext);
        }
      }
    }
  }

  public void Save(HueCredentials credentials) {
    ArgumentNullException.ThrowIfNull(credentials);

    lock(_gate) {
      Directory.CreateDirectory(_directory);

      byte[] plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(credentials));
      try {
        byte[] ciphertext = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);

        // Written beside the target and moved over it, so a crash halfway through
        // leaves the previous credentials intact rather than a truncated file that
        // reads as corrupt. Re-pairing is a walk to the bridge; it is worth avoiding
        // over something this cheap.
        string temporary = Path + ".tmp";
        File.WriteAllBytes(temporary, ciphertext);
        File.Move(temporary, Path, overwrite: true);
      } finally {
        CryptographicOperations.ZeroMemory(plaintext);
      }
    }
  }

  public void Clear() {
    lock(_gate) {
      // File.Delete tolerates a file that is not there and throws for a directory
      // that is not, which is a distinction nothing outside this class should have
      // to know about: "forget the bridge" is offered whether or not anything was
      // ever paired, and on a machine that never paired neither exists.
      if(!Directory.Exists(_directory)) {
        return;
      }

      File.Delete(Path);
      File.Delete(Path + ".tmp");
    }
  }
}
