---
name: publicar
description: Push main to the github remote and, only when the user asks for a release ("release" argument), build the self-contained zip and publish a GitHub release with it. Use after a round of fixes is committed.
context: fork
agent: general-purpose
model: sonnet
argument-hint: "[release]"
---

You publish the YGO-Omegabot repo (C:\Users\Javier\Documents\YGO-Omegabot). Arguments: `$ARGUMENTS`.

Mode:
- If the arguments contain `release` → **push + release**.
- Otherwise → **push only**. Never make a release unless `release` was passed.

## Rules
- Push only to the `github` remote (https://github.com/javiergmonteiro/YGO-Omega-Bot.git). **Never push to `origin`** (upstream GitLab).
- Never force-push. If the push is rejected (non-fast-forward), stop and report; don't rebase or merge on your own.
- Don't commit anything yourself. If `git status` shows uncommitted changes to tracked files, report them and push what is committed anyway (untracked files like `.vscode/` are ignored).

## 1. Push (both modes)
```
git -C C:/Users/Javier/Documents/YGO-Omegabot push github main
```
Note how many commits went up (`git rev-list --count github/main@{1}..github/main` or compare before/after).

## 2. Release (only in release mode)
1. **Build the zip** (same as `Empaquetar.cmd`, but without its `pause`). From the repo root in bash:
   ```
   rm -rf dist/BotYGOOmega dist/BotYGOOmega.zip
   dotnet publish BotTest/BotTest.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/BotYGOOmega -nologo -v q
   rm -f dist/BotYGOOmega/*.pdb dist/BotYGOOmega/web.config
   cp "dist-template/Iniciar Bot.cmd" dist-template/LEEME.txt dist/BotYGOOmega/
   powershell -NoProfile -Command "Compress-Archive -Path 'dist\BotYGOOmega' -DestinationPath 'dist\BotYGOOmega.zip' -Force"
   ```
   If the publish fails, stop and report the errors. Don't release.
2. **Tag**: `vYYYY.MM.DD` with today's date; if that tag already exists (`git tag -l`), add `-2`, `-3`, … Create it on `HEAD` and push it: `git push github <tag>`.
3. **Notes** (in Spanish, Argentina): list the commit subjects since the previous `v*` tag (`git log <prevtag>..HEAD --format=%s`; if there is none, the last ~30 commits), grouped briefly by deck/theme as bullets. Short, plain language for players. End with how to install: "Descomprimir `BotYGOOmega.zip` y abrir `Iniciar Bot.cmd`."
4. **Publish**:
   ```
   gh release create <tag> dist/BotYGOOmega.zip -R javiergmonteiro/YGO-Omega-Bot --title "Bot YGO Omega <tag>" --notes-file <notes file in a temp dir>
   ```

## Report back
In Spanish (Argentina), 2–4 lines: commits pushed, and in release mode the tag, the release URL and the zip size. Report any failure exactly.
