# ArtistArt

A Lidarr plugin that keeps several images per artist in the artist's own folder and lets you choose which one to use from inside Lidarr.

![Picker](docs/2-picker.png)

## What it does

- Every artist folder gets an `Artwork/` subfolder that holds all candidate images: posters, fanart, banners and logos.
- On first use, images already in the artist folder (`folder.jpg`, `artist.jpg`, `fanart.jpg` and so on) are copied into `Artwork/`. Lidarr's own downloaded images are also saved there as `poster-lidarr.jpg` etc., so you can always go back to them.
- The **Artwork** page in Lidarr (`/artwork`, also linked from each artist's Links) shows every candidate. Click one to use it, or upload new ones.
- The chosen image is written to the artist folder for media servers (`artist.*` and `folder.*` for the poster, `fanart.*`, `banner.*`, `logo.*`). It is also written into Lidarr's own image cache, so the Lidarr UI shows it too and Lidarr's metadata refresh does not undo it.
- Navidrome picks up `artist.*` with its default `ArtistArtPriority`.

![Artist page](docs/4-artist-page-after.png)

## Install

1. Use a Lidarr build that supports plugins (the `develop`/plugins branch).
2. Go to **System → Plugins**, paste `https://github.com/noplexzone/ArtistArt` and install. Restart Lidarr.
3. Go to **Settings → Metadata** and add **Artist Art**. Saving it turns the plugin on.
4. Set **Settings → General → Application URL** (for example `http://lidarr.local:8686`) so the Artwork link on artist pages points to the right place. You can also set **Picker Address** in the plugin's advanced settings instead.

## Using it

Open an artist, click **Artwork** under Links, or go to `http://<lidarr>/artwork` and choose an artist.

- Click a thumbnail to use it. The badge shows which image is in use.
- **Add image** uploads a .jpg, .png or .webp file (up to 50 MB) into the artist's `Artwork/` folder.
- **Use Lidarr's image** goes back to the image Lidarr downloaded.
- Hover a thumbnail to delete it. The image in use can't be deleted.

You can also drop files into `Artwork/` yourself. Files are grouped by name prefix:

| Type | File names |
|---|---|
| Poster | `poster*`, `artist*`, `folder*` |
| Fanart | `fanart*`, `backdrop*`, `background*` |
| Banner | `banner*` |
| Logo | `logo*`, `clearlogo*` |

A prefix must be the whole name or be followed by `-`, `_`, `.` or a space, for example `poster-live.jpg` or `fanart 2.png`.

The selection is saved in `Artwork/artwork.json` inside the artist folder, so it survives a Lidarr reinstall.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| Candidate Folder | `Artwork` | Subfolder of the artist folder that holds candidates |
| Write artist.jpg | on | Write the chosen poster as `artist.*` (Navidrome) |
| Write folder.jpg | on | Write the chosen poster as `folder.*` (Plex, Jellyfin, Kodi) |
| Write Other Images | on | Write `fanart.*`, `banner.*`, `logo.*` |
| Keep Lidarr's Images | on | Keep a copy of Lidarr's own images as `*-lidarr.*` candidates |
| Artwork Link | on | Add an Artwork link to each artist's Links |
| Picker Address (advanced) | empty | Overrides Application URL for that link |

## Known limits

- After a metadata refresh, Lidarr briefly shows its own image again before the plugin puts yours back (usually under a second).
- Lidarr's artist page only shows types its metadata already has. If Lidarr has no fanart for an artist, a chosen fanart is still written to the folder but won't appear in Lidarr.
- The Artwork page is a separate page served by Lidarr, not a tab inside the artist page. Plugins can't add to Lidarr's built-in UI.

## Building

```sh
git clone --recursive https://github.com/noplexzone/ArtistArt
dotnet test tests/ArtistArt.Tests/ArtistArt.Tests.csproj -p:AssemblyVersion=0.1.0.0
dotnet build src/ArtistArt/ArtistArt.csproj -c Release -p:Version=0.1.0 -p:AssemblyVersion=0.1.0.0 -o _output
```

Pass `AssemblyVersion` on the command line, not in the project file. It has to apply to the Lidarr projects too, or the plugin binds to Lidarr.Core 10.0.0.x and won't load.

Releases are built by GitHub Actions when a `v*` tag is pushed. The release asset is `ArtistArt.net8.0.zip`, which is what Lidarr's plugin installer looks for.
