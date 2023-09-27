using System;
#if IOS
using ObjCRuntime;
#endif

#if IOS
[assembly: LinkWith(LinkerFlags = "-lSDL-2.0")]
#endif