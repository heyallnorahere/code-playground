# code-playground

Graphics framework written for .NET 7.

## Building

Building the project requires the following:
- [.NET 7 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/7.0)
- [CMake](https://cmake.org) >= v3.20
- A C++ compiler that supports C++17

Additionally, depending on your system, you may need to build and install the following projects to avoid runtime crashes:
- [GLFW](https://github.com/glfw/glfw) or `libglfw3` on Ubuntu/Debian
- [Assimp](https://github.com/assimp/assimp) or `libassimp5` on Ubuntu/Debian
- [shaderc](https://github.com/google/shaderc)
- [Vulkan-Loader](https://github.com/KhronosGroup/Vulkan-Loader) or the [Vulkan SDK](https://vulkan.lunarg.com/) or `libvulkan1` on Ubuntu/Debian
- [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross) or `libspirv-cross-c-shared0` on Ubuntu/Debian
- [cimgui](https://github.com/cimgui/cimgui)
- [Tracy profiler](https://github.com/wolfpld/tracy)

To build the project, simply run:
```bash
# making sure we have all of the submodules up to date
# where $ROOT_DIR is the root directory of the repository
cd $ROOT_DIR
git submodule update --init --recursive

# building libchess
# where $GENERATOR is the build system for your machine
# this is required because the chess AI project relies on my native library
# todo(nora): include prebuilt binaries
cd $ROOT_DIR/Libraries/chess
cmake . -B build -G "$GENERATOR" -DCMAKE_BUILD_TYPE=$CONFIG # required for single-config generators
cmake --build build -j 8 --config $CONFIG # required for multi-config generators

# and $CONFIG is the application configuration (Debug or Release)
cd $ROOT_DIR
dotnet build -c $CONFIG Solutions/code-playground.sln
```

## Running

```bash
# after building
# where $ROOT_DIR is the root directory of the repository
# $APPLICATION is the name of the application (e.g. VulkanTest)
# and $CONFIG is the application configuration
cd $ROOT_DIR/Playground/$APPLICATION/bin/$CONFIG/net7.0

# the framework loads the application dynamically
dotnet CodePlayground.Runtime.dll $APPLICATION.dll ...

# alternatively, if the application implements its own entrypoint
dotnet $APPLICATION.dll ...
```

## "Playground" applications

- VulkanTest: Vulkan graphics test application
- Ragdoll: WIP ragdoll simulation of skinned meshes. Not finished
- MachineLearning: Neural network running in a compute pipeline. Recognizes handwritten digits
- ChessAI: WIP chess neural network - does not work lol