# TokenRateGate Release Process

This document describes how to release a new version of TokenRateGate to NuGet.org.

## Prerequisites

1. **NuGet API Key**: Obtain an API key from https://www.nuget.org/account/apikeys
   - Log in to NuGet.org
   - Go to Account → API Keys
   - Create a new API key with "Push" permission for all packages
   - Copy the key (you won't be able to see it again!)

2. **GitHub Secret**: Add the NuGet API key to GitHub
   - Go to your repository on GitHub
   - Navigate to Settings → Secrets and variables → Actions
   - Click "New repository secret"
   - Name: `NUGET_API_KEY`
   - Value: Your NuGet API key from step 1

3. **Optional - Codecov**: For code coverage reports
   - Sign up at https://codecov.io with your GitHub account
   - Add your repository
   - Copy the token
   - Add as GitHub secret named `CODECOV_TOKEN`

## Release Workflow

### 1. Prepare the Release

1. **Ensure all tests pass**:
   ```bash
   dotnet test TokenRateGate.sln
   ```

2. **Update version in Directory.Build.props**:
   ```xml
   <Version>X.Y.Z</Version>
   <AssemblyVersion>X.Y.Z.0</AssemblyVersion>
   <FileVersion>X.Y.Z.0</FileVersion>
   ```

3. **Update release notes in Directory.Build.props** (optional - can also edit on GitHub):
   ```xml
   <PackageReleaseNotes>
     vX.Y.Z
     - Feature: Description
     - Fix: Description
     - Breaking: Description (if any)
   </PackageReleaseNotes>
   ```

4. **Commit the version bump**:
   ```bash
   git add Directory.Build.props
   git commit -m "chore: bump version to X.Y.Z"
   git push origin master
   ```

### 2. Create and Push a Git Tag

The release workflow is triggered by creating a version tag:

```bash
# Create an annotated tag
git tag -a vX.Y.Z -m "Release vX.Y.Z"

# Push the tag to GitHub
git push origin vX.Y.Z
```

**Tag naming convention**:
- Stable releases: `v1.0.0`, `v1.1.0`, `v2.0.0`
- Pre-releases: `v1.0.0-alpha.1`, `v1.0.0-beta.2`, `v1.0.0-rc.1`

### 3. Automated Release Process

Once you push the tag, GitHub Actions will automatically:

1. **Build** all projects in Release configuration
2. **Run all tests** to ensure quality
3. **Pack NuGet packages** for all 8 libraries
4. **Publish to NuGet.org** (all 8 packages)
5. **Create GitHub Release** with:
   - Release notes (generated from commits since last tag)
   - Links to all NuGet packages
   - Package artifacts attached

### 4. Monitor the Release

1. **Check GitHub Actions**:
   - Go to Actions tab in your repository
   - Watch the "Release" workflow
   - Verify all steps complete successfully

2. **Verify NuGet.org**:
   - Check https://www.nuget.org/packages/TokenRateGate.Core/
   - Packages may take 5-15 minutes to appear in search
   - Verify README, icon, and metadata display correctly

3. **Verify GitHub Release**:
   - Go to Releases in your repository
   - Verify release notes are accurate
   - Download artifacts to confirm they're correct

## Version Numbering Strategy

TokenRateGate follows [Semantic Versioning](https://semver.org/):

- **MAJOR** (X.0.0): Breaking API changes
- **MINOR** (0.X.0): New features, backward compatible
- **PATCH** (0.0.X): Bug fixes, backward compatible

### Pre-release versions:
- `0.9.0`: Public beta (current)
- `1.0.0-rc.1`: Release candidate
- `1.0.0`: First stable release

## Troubleshooting

### Release workflow fails at NuGet push

**Problem**: `error: Response status code does not indicate success: 409 (Conflict)`

**Solution**: Version already exists on NuGet.org. You cannot overwrite published packages. Increment version and try again.

---

**Problem**: `error: The API key is invalid`

**Solution**: Check that `NUGET_API_KEY` secret is set correctly in GitHub repository settings.

### Packages don't appear on NuGet.org

**Wait**: New packages can take 5-15 minutes to index and appear in search results. Check the direct URL first:
- https://www.nuget.org/packages/TokenRateGate.Core/X.Y.Z

### Wrong version number in packages

**Problem**: Some packages have different versions

**Solution**: Ensure no `<Version>` tags in individual `.csproj` files. Only `Directory.Build.props` should define the version.

## Manual Release (Emergency)

If GitHub Actions are unavailable, you can release manually:

```bash
# 1. Build packages locally
dotnet pack TokenRateGate.sln --configuration Release --output ./packages

# 2. Push to NuGet.org
dotnet nuget push "./packages/*.nupkg" \
  --api-key YOUR_NUGET_API_KEY \
  --source https://api.nuget.org/v3/index.json \
  --skip-duplicate

# 3. Create GitHub release manually
gh release create vX.Y.Z ./packages/*.nupkg \
  --title "Release vX.Y.Z" \
  --notes "Release notes here"
```

## Post-Release Checklist

- [ ] Verify all 8 packages published to NuGet.org
- [ ] Check package metadata (icon, README, description)
- [ ] Test installation: `dotnet add package TokenRateGate --version X.Y.Z`
- [ ] Update documentation if needed
- [ ] Announce release (optional): Twitter, LinkedIn, Reddit, etc.
- [ ] Close related GitHub issues/milestones

## First-Time Setup Summary

For your first release (v0.9.0):

1. Get NuGet API key from https://www.nuget.org/account/apikeys
2. Add `NUGET_API_KEY` secret to GitHub repository
3. Run tests: `dotnet test`
4. Commit any final changes
5. Create and push tag: `git tag -a v0.9.0 -m "Public beta release" && git push origin v0.9.0`
6. Watch GitHub Actions complete the release
7. Verify packages on NuGet.org after 5-15 minutes

That's it! The automation handles everything else.
