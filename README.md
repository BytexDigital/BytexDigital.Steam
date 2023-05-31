![Nuget](https://img.shields.io/nuget/vpre/BytexDigital.Steam.svg?style=flat-square)

# BytexDigital.Steam

This library is a wrapper for the popular SteamKit2 library. It's primary goal is to simplify specific APIs of it to
make them easier to use.

As of now, this library's main purpose is to allow for simple content downloading (apps and workshop items) off Steam.

> :warning: **Please note that this library is in early beta. APIs may change heavily between versions.**

## Download

[nuget package](https://www.nuget.org/packages/BytexDigital.Steam/)

### Simple item download usage example

See the [test client project](https://github.com/BytexDigital/BytexDigital.Steam/tree/master/BytexDigital.Steam.TestClient)

#### Filter which files to download

You can use a condition to limit your download to specific files.

```csharp
var downloadTask = downloadHandler.DownloadToFolderAsync(@".\downloads", x => x.FileName.EndsWith(".exe"));
```
