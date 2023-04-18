# GifImporter

A [NeosModLoader](https://github.com/zkxs/NeosModLoader) mod for [Neos VR](https://neos.com/) that allows you to
essentially import gif images. It converts gifs to a spritesheet then adds the appropriate components.

Related issue on [NeosPublic](https://github.com/Neos-Metaverse/NeosPublic/) issue tracker:
[261](https://github.com/Neos-Metaverse/NeosPublic/issues/261)

## Usage

![preview](.img/preview.gif)

Simply import a `gif` image like you would any other image (such as the `gif` above).

## Square tiles

![filesize](.img/filesize.jpg)

As noted in the config, square tiles can sometimes make a bigger size tilesheet.

## Note for linux users

Install `libgdiplus` for your distro.

## Installation
1. Install [NeosModLoader](https://github.com/zkxs/NeosModLoader).
2. Place [GifImporter.dll](https://git.astralchan.xyz/astral/GifImporter/releases/download/1.1.4/GifImporter.dll) into
your `nml_mods` folder. This folder should be at `C:\Program Files (x86)\Steam\steamapps\common\NeosVR\nml_mods` for a
default install. You can create it if it's missing, or if you launch the game once with NeosModLoader installed it will
create the folder for you.
3. Start the game. If you want to verify that the mod is working you can check your Neos logs.