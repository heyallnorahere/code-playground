using System;
#if IOS
using ObjCRuntime;
#endif

#if IOS
[assembly: LinkWith("libSDL2.a", LinkTarget.x86_64 | LinkTarget.Arm64, SmartLink = true, ForceLoad = true)]
[assembly: LinkWith("libSDL2.a", LinkTarget.x86_64 | LinkTarget.Arm64, SmartLink = true, ForceLoad = true)]
#endif