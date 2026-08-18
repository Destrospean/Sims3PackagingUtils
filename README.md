This repository is a WIP collection of tools for Sims 3 modding. Hereinafter are descriptions of the listed tools.

### Tuning Resource Generator

This is a tool to automatically generate tuning _XML resources from the assemblies in a .package file for The Sims 3.

It works by the user dragging a package with the assemblies imported on top of the executable and said executable generates tunables and their comments (along with any assigned values in the code) as _XML resources, with the instance hash being that of the namespace + class of the class each tuning file corresponds to. It accounts for different classes by making a separate _XML resource for each class with tunable fields, and also works for packages with multiple assemblies.

For Linux users, instead of dragging and dropping, it's best to put in a local bin folder that is included in your `PATH` environment variable, along with a shell script in the same folder simply called tunresgen with the following contents:
```
#!/bin/bash
mono "${0%/*}"/TuningResourceGenerator.exe "$@"
``` 

Make sure that shell script is executable.
You can then open a terminal in the folder of the package you're trying to add tuning _XML resources to and do the following:
```
tunresgen PACKAGE_NAME.package
```
