# RXDK MSBuild Integration (experimental)

This is an attempt to bring the experience of using RXDK with Visual Studio up to the same level as
official console SDKs offer. I wrote up some [notes](NOTES.md) about how it works.

It's currently experimental, but it's able to build a proper XBE.

## Usage

For now, this isn't included in the VSIX. To use it, copy the `RXDK` folder to here:
```
C:\Program Files\Microsoft Visual Studio\<your VS version>\Enterprise\MSBuild\Microsoft\VC\v170\Application Type
```

Here are some notes if you're planning to work on it:
- Any modifications to the files won't be picked up by Visual Studio until you restart it
- Unless you're working on property pages, using MSBuild from the command line lets you iterate faster
- Symlinking the `RXDK` folder into `Application Types` is super handy

## Sample project

See the [test folder](test/) for a sample project, you can probably modify it enough for your purposes.
