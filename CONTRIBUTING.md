# Contributing

Thanks for contributing to SelfUpdater!

## Prerequisites

- The .NET SDK matching the repo's target framework (`net10.0`).

## Build and test

```
dotnet restore SelfUpdater.slnx
dotnet build SelfUpdater.slnx -c Release
dotnet test SelfUpdater.slnx -c Release
```

## Formatting (required)

All C# code must be formatted with [CSharpier](https://csharpier.com/). It's
pinned as a local dotnet tool, so no global install is needed:

```
dotnet tool restore        # once, after cloning
dotnet csharpier format .  # format the whole repo
dotnet csharpier check .   # verify formatting (what CI runs)
```

CI fails any change that isn't CSharpier-formatted, so run `dotnet csharpier
format .` before committing (or wire it into your editor / a local pre-commit
hook).
