# code-playground

Simple framework for creating simple projects

## Building

This project uses packages from GitHub package registries. As such, it's required that you use a personal access token. To create an appropriate config file for NuGet, paste the following temple into a `nuget.config` file in this directory, and replace the appropriate sections:
```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
    <packageSources>
        <add key="github" value="https://nuget.pkg.github.com/yodasoda1219/index.json" />
    </packageSources>
    <packageSourceCredentials>
        <github>
            <add key="Username" value="YOUR_USERNAME" />
            <add key="ClearTextPassword" value="YOUR_ACCESS_TOKEN" />
            <!-- token must have the read:packages permission -->
        </github>
    </packageSourceCredentials>
</configuration>
```