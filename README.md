# VanillaCoreifier
A simple tool to patch Celeste to run under .NET Core, instead of .NET Framework 4.5.2.

Tested with itch.io copies of all platform, but should work with any copy, if not please report!

Remarks: the version used for the runtime will be the same one as the one from the latest everest stable version.

## Usage
- `--input`: Path to the target executable, should always be called `Celeste.exe` on all platfomrs, defaults to `./Celeste.exe`
- `--output`: Name of the file executable that holds the CIL, defaults to `CelesteVCore.dll`, should be not specified on most cases.
- `--remote`: Gets the required files from everest by downloading an everest copy itself.
- `--everestpath`: Provides the path of an everest installation, defaults to `..`. Overriden by `--remote`.
- `--platform`: Forces the patching to be done to a specific platform, possible values are: `linux`, `windows` and `macos`. App-resource copying is only supported when running under windows.

Does not support any 32 bit OS.

## Extra
This tool will not mess with vanilla code what so ever, it only does the necessary changes for it to still run properly.

It does though update the native libraries to the ones used by everest, though there shouldn't be any differences with that.

Assembly patching is uniquely done by `NETCoreifier` from the [everest repository](https://github.com/EverestAPI/Everest/tree/dev/NETCoreifier), check it out if you want to learn more.
