---
name: publicar
description: Push main to the github remote (no release). Use after a round of fixes is committed. For a release use publicar-release.
context: fork
agent: general-purpose
model: sonnet
---

You publish the YGO-Omegabot repo (C:\Users\Javier\Documents\YGO-Omegabot). 

Mode: **push only**. Never make a release (that's the `publicar-release` skill).

## Rules
- Push only to the `github` remote (https://github.com/javiergmonteiro/YGO-Omega-Bot.git). **Never push to `origin`** (upstream GitLab).
- Never force-push. If the push is rejected (non-fast-forward), stop and report; don't rebase or merge on your own.
- Don't commit anything yourself. If `git status` shows uncommitted changes to tracked files, report them and push what is committed anyway (untracked files like `.vscode/` are ignored).

## Push
```
git -C C:/Users/Javier/Documents/YGO-Omegabot push github main
```
Note how many commits went up (`git rev-list --count github/main@{1}..github/main` or compare before/after).

## Report back
In Spanish (Argentina), 2–4 lines: commits pushed. Report any failure exactly.
