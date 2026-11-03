# zinc

Command line tool for the [Zinc](https://github.com/zinc-framework/Zinc) game framework. Needs the .NET 10 SDK or later; nothing to install:

```
dnx zinc new mygame
cd mygame
dnx zinc package mac
```

`dnx` downloads the tool on first use and runs it. Inside a project that pins zinc in its tool manifest (`zinc new` does this), `dnx zinc` runs the pinned version.

## Commands

### `zinc new [NAME]`

Creates a game from [Zinc.Template](https://github.com/zinc-framework/Zinc.Template): a fresh git repository with the template's submodules (Zinc) at the commits the template pins, the solution and assembly named after the game, and this version of zinc pinned. `--template <repo>` and `--branch <branch>` start from something else.

### `zinc package mac`

Publishes the game and packages it as `dist/macos/<App>.app`.

```
zinc package mac                                  # ad-hoc signed, for testing on this Mac
zinc package mac --sign                           # signed with your Developer ID
zinc package mac --sign --notarize my-profile     # signed, notarized, stapled, zipped: ready to ship
zinc package mac -- -p:DefineConstants=DEMO       # anything after -- goes to dotnet publish
```

zinc always runs `dotnet publish` itself (Release, self-contained, single-file, no `.pdb` files, into `dist/.stage/`) so a package never picks up a stale or Debug build. Every command it runs is printed, so any step can be rerun by hand. To package a folder you published yourself, pass `--from <dir>`; it has to be a Release, single-file publish with the `zinc.publish.json` Zinc writes.

In the bundle, the executable and native libraries go in `Contents/MacOS` and everything else (`res/`, `data/`) in `Contents/Resources`, where Zinc looks for content when it runs from a bundle.

`Info.plist`'s `LSMinimumSystemVersion` is the newest minimum macOS (`minos`) among the bundle's binaries, as `otool -l` reports it, since macOS won't load any of them on an older one. With the key there, an older Mac says which macOS the app needs instead of the app failing to launch. The .NET runtime sets the floor (macOS 14 for .NET 11) unless a native library needs newer, and zinc prints which version it found.

### `zinc package windows`

Publishes the game for `win-x64` and packages it as `dist/windows/<App>/`, plus `dist/windows/<App>-windows.zip` holding that folder.

```
zinc package windows                              # the folder to upload as a Steam depot, and a zip of it
zinc package windows -- -p:PublishSingleFile=false
```

It publishes the same way as on macOS (`--from`, `--project`, `-o` and `--` work the same), and also single-file by default. That isn't required on Windows, but it leaves players with the `.exe`, the native libraries and the game's content instead of a few hundred assemblies. The folder is the publish as it is, since Zinc finds content next to the executable on Windows.

The `.exe` gets its icon from `ZincAppIcon`, which zinc turns into an `.ico` before publishing because the SDK builds the icon into the executable. A game that sets its own `<ApplicationIcon>` keeps it. The SDK only adds the icon and version info when it builds on Windows, so package on Windows. The `.exe` isn't code signed: installs through Steam don't need it, but SmartScreen warns about an unsigned executable downloaded from the web.

### `zinc doctor`

Checks the machine and project: .NET SDK, git, Xcode tools, signing identities, the project's packaging settings. `--notary-profile <name>` also checks notarization credentials. Lists how to fix whatever's missing.

## Project settings

zinc reads packaging settings from the game's `.csproj`, through the `zinc.publish.json` that Zinc's `Zinc.Publish.targets` writes into every publish:

```xml
<PropertyGroup>
  <ZincAppName>My Game</ZincAppName>                 <!-- defaults to AssemblyName -->
  <ZincBundleId>com.yourstudio.mygame</ZincBundleId> <!-- required to sign -->
  <ZincAppIcon>res/icon.png</ZincAppIcon>            <!-- relative to the publish folder -->
</PropertyGroup>

<ItemGroup>
  <!-- macOS: entitlements beyond allow-jit, for a signed app (see below) -->
  <ZincEntitlement Include="com.apple.security.cs.disable-library-validation" />
</ItemGroup>

<Import Project="..\Zinc\Zinc.Publish.targets" />
```

The manifest has a `schema` number, and zinc only packages the one it understands: if it says to update zinc or the game's Zinc, the two are out of step.

### Entitlements

A signed app runs under the hardened runtime, which notarization requires. zinc gives it `com.apple.security.cs.allow-jit`, since .NET needs it, plus each `ZincEntitlement`, set true. An ad-hoc signed app has no hardened runtime, so none of this applies to it, which also means something that works ad-hoc can still fail once signed.

The usual one is `com.apple.security.cs.disable-library-validation`: the hardened runtime only loads libraries signed by Apple or by the app's own team, so a library that loads another developer's code at runtime fails without it. Steamworks is one: `libsteam_api.dylib` loads `steamclient.dylib` from the player's Steam install, signed by Valve, and `SteamAPI_Init` fails. The Steam overlay also needs `com.apple.security.cs.allow-dyld-environment-variables`, since Steam injects it with `DYLD_INSERT_LIBRARIES`.

## Signing and notarizing on macOS

One-time setup (`zinc doctor` walks through it):

1. Create a **Developer ID Application** certificate: Xcode → Settings → Accounts → your team → Manage Certificates → + → Developer ID Application. Only the team's Account Holder can.
2. Make an app-specific password at [account.apple.com](https://account.apple.com) (Sign-In and Security → App-Specific Passwords).
3. Save notarization credentials to the keychain:
   ```
   xcrun notarytool store-credentials my-profile --apple-id <your Apple ID> --team-id <TEAMID>
   ```

Then `zinc package mac --sign --notarize my-profile`.

## Developing

```
dotnet run -- doctor                      # run from source
dotnet pack -c Release                    # artifacts/zinc.0.0.0-local.nupkg
dnx zinc@0.0.0-local --add-source ./artifacts --yes -- doctor
```

## Releasing

Push a version tag; the version comes from the tag, not the `.csproj`:

```
git tag v0.1.0 && git push origin v0.1.0
```

`.github/workflows/publish.yml` packs that version and publishes it to nuget.org through Trusted Publishing, so no API key is stored anywhere.
