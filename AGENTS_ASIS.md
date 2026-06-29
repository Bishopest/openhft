This file provides unified instructions for CODEX when working in this repository.

The goal of this document is to combine repository-specific architecture guidance with execution, communication, and implementation constraints so that instructions are interpreted consistently and in a stable priority order.

## Instruction Priority

When instructions overlap or appear to conflict, follow this priority order:

1. Explicit user request
2. Repository safety and execution constraints in this file
3. Repository architecture and workflow guidance in this file
4. General implementation preferences in this file

If a task requires file creation or modification, verify that the required execution trigger is present before making changes.

## Core Communication Rules

- Respond to the user in Korean, regardless of the language used by the user.
- Start every response with a percentage indicating your understanding of the prompt.
- Write all code comments, docstrings, and generated documentation content in English.
- Verify information before presenting it. Do not speculate without evidence.
- Do not ask the user to confirm information that is already available in repository context.
- Always reference real files in the workspace when discussing implementation locations.****

## Strict File Editing and Creation Rule

You must not create, edit, or modify files unless one of the following is true:

1. The user explicitly says "Start implementation"
2. The user approves the implementation plan
3. The environment indicates plan approval / implementation mode has started

## Mandatory Workspace Context Rule

Before proposing architectural changes or implementation details, inspect the workspace and relevant files to understand the current project structure and existing implementation.

## Skills Usage Rule

Always use available Skills when relevant. If no suitable skill exists, complete the task directly.

## Quick Start

### Python Environment

All Python work requires the Conda environment named `lean`:

```bash
conda activate lean
```

Do not create or use other Python virtual environments such as `venv`, `virtualenv`, or `poetry` environments for this repository.

When executing Python-related commands:

1. Verify the active environment.
2. If the active environment is not `lean`, activate `lean` first.
3. Prefer repository-consistent execution patterns and existing tooling.

## Repository Overview

This repository is a cryptocurrency statistical arbitrage trading system with several major components:

- cross-sectional alpha research
- beta-neutral portfolio construction
- feature engineering
- exchange data collection
- backtesting
- machine learning workflows

## Codebase Architecture

## Implementation Guidelines

### Scope and Change Discipline

- Only implement what the user explicitly requested.
- Preserve unrelated functionality.
- Do not remove unrelated code.
- Do not propose whitespace-only or formatting-only changes unless required.
- Make changes file by file.
- Provide edits in a complete chunk per file rather than fragmented partial edits.

### Code Quality

- Prefer descriptive, explicit variable names.
- Follow the existing repository coding style.
- Prioritize performance and security.
- Replace hardcoded values with named constants where appropriate.
- Handle edge cases explicitly.
- Add assertions where they clarify assumptions.
- Use robust error handling and logging when needed.
- Keep implementations modular and reusable.
- Ensure compatibility with the project’s framework and language versions.
- Suggest or include unit tests for new or modified behavior.

### Verification

- Verify assumptions using the actual codebase before presenting conclusions.
- Do not rely on guessed repository structure or inferred implementation details.
- Check the current file contents before proposing modifications.
