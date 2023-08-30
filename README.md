# code-playground

.NET 7 framework for simple setup of graphics projects

## Building

This project uses [.NET 7](https://dotnet.microsoft.com/en-us/download/dotnet/7.0). Additionally, depending on your system, you may have to build and install the following projects to avoid runtime crashes:
- [GLFW](https://github.com/glfw/glfw) or `libglfw3` on Ubuntu/Debian
- [Assimp](https://github.com/assimp/assimp) or `libassimp5` on Ubuntu/Debian
- [shaderc](https://github.com/google/shaderc)
- [Vulkan-Loader](https://github.com/KhronosGroup/Vulkan-Loader) or the [Vulkan SDK](https://vulkan.lunarg.com/) or `libvulkan1` on Ubuntu/Debian
- [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross) or `libspirv-cross-c-shared0` on Ubuntu/Debian
- [cimgui](https://github.com/cimgui/cimgui)
- [Optick](https://github.com/bombomby/optick) with Vulkan & (Windows) DirectX support ([Vulkan validation fix](https://github.com/qbojj/optick/tree/fix-vulkan))
- `coptick` from my [.NET Optick bindings](https://github.com/yodasoda1219/Optick.NET)

This project uses packages from GitHub package registries. As such, it's required that you use a personal access token. To create an appropriate config file for NuGet, paste the following template into a `nuget.config` file in the root directory, and replace the appropriate sections:
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

To build the project, simply run:
```bash
# assuming the .NET executable is on the PATH
# where $ROOT_DIR is the root directory of the repository
cd $ROOT_DIR

# and $CONFIG is the application configuration (Debug or Release)
dotnet build -c $CONFIG
```

**DISCLAIMER**: As of this commit, Release builds crash during shader translation. This is due to instruction optimizations during compilation that are not handled in translation. This will be addressed eventually.

## Running

```bash
# after building
# where $ROOT_DIR is the root directory of the repository
# $APPLICATION is the name of the application (e.g. VulkanTest)
# and $CONFIG is the application configuration
cd $ROOT_DIR/Playground/$APPLICATION/bin/$CONFIG/net7.0

# the framework loads the application dynamically
dotnet CodePlayground.dll $APPLICATION.dll ...
```

## "Playground" applications

- VulkanTest: Vulkan graphics test application
- Ragdoll: WIP ragdoll simulation of skinned meshes. Not finished
- MachineLearning: Neural network running in a compute shader
- ChessAI: WIP chess neural network