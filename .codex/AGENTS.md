# Codex Agent Configuration

This repository uses `dotnet-claude-kit` as the base .NET intelligence layer, extended with repository-specific trading system constraints.

This document defines unified instructions for Codex when working in this repository.

Its purpose is to combine:

- repository-specific architecture guidance
- execution and safety constraints
- communication rules
- skill and agent orchestration
- Roslyn-first code intelligence workflow

---

# Instruction Priority

When instructions overlap or conflict, follow this order:

1. Explicit user request
2. Repository safety and execution constraints in this file
3. Repository architecture and workflow guidance in this file
4. dotnet-claude-kit skills, rules, and agent conventions
5. General implementation preferences in this file

If a task requires file creation or modification, verify that implementation approval exists before making changes.

---

# Core Communication Rules

- Always respond in Korean regardless of the user's language.
- Start every response with a percentage indicating confidence/understanding.
- Write all code comments, docstrings, and generated documentation in English.
- Verify information before presenting it.
- Never speculate without evidence.
- Do not ask for confirmation if the information already exists in the workspace.
- Always reference real files in the workspace when discussing implementation.

---

# Strict File Editing and Creation Rule

Do not create, edit, or modify files unless one of the following is true:

1. The user explicitly says **"Start implementation"**
2. The user approves the implementation plan
3. The environment indicates implementation mode has started

Otherwise:

- prepare a plan
- provide patch outlines
- explain intended changes
- wait for approval

---

# Mandatory Workspace Context Rule

Before proposing architecture changes or implementation:

- inspect the workspace
- inspect relevant files
- inspect dependencies
- inspect current implementations

Never assume repository structure.

---

# Roslyn-first Navigation Rule (MCP Mandatory)

Always prefer Roslyn MCP tools over reading full source files.

Use MCP first for:

- symbol lookup
- reference lookup
- implementations
- inheritance chains
- call graphs
- dependency graphs
- dead code detection
- diagnostics
- public API inspection

Only read full files if:

1. Roslyn lookup fails
2. implementation details are required
3. local logic inspection is necessary

This is mandatory for context efficiency.

---

# Available Agents

Use specialized agents when relevant.

| Agent | Purpose |
|-------|---------|
| dotnet-architect | Architecture, project structure, module boundaries |
| api-designer | API design, versioning, OpenAPI |
| ef-core-specialist | EF Core queries, migrations, DbContext design |
| test-engineer | Testing strategy, NUnit, integration tests |
| security-auditor | Security review, auth, vulnerabilities |
| performance-analyst | Profiling, optimization, benchmarks |
| devops-engineer | CI/CD, Docker, deployment |
| code-reviewer | Code review and quality analysis |
| build-error-resolver | Build failure diagnosis and repair |
| refactor-cleaner | Cleanup, dead code removal, safe refactoring |

Always use the most appropriate agent when available.

---

# Skills Usage Rule

Always use available Skills when relevant.

If no suitable skill exists:

- solve directly
- follow repository conventions
- preserve consistency

---

# Available Skills

Skills live in:

`skills/<skill-name>/SKILL.md`

## .NET Domain Skills

- api-versioning
- architecture-advisor
- aspire
- authentication
- caching
- ci-cd
- clean-architecture
- configuration
- ddd
- dependency-injection
- docker
- ef-core
- error-handling
- httpclient-factory
- logging
- messaging
- minimal-api
- modern-csharp
- openapi
- opentelemetry
- project-setup
- project-structure
- resilience
- scalar
- serilog
- testing
- vertical-slice
- container-publish

## Workflow Skills

- build-fix
- checkpoint
- code-review
- de-sloppify
- dotnet-init
- health-check
- migrate
- plan
- scaffold
- security-scan
- spec
- tdd
- verify
- wrap-up

## Learning Skills

- convention-learner
- workflow-mastery
- instinct-system

---

# MCP Tools

Use `cwm-roslyn-navigator` for code intelligence.

Available tools:

- find_symbol
- find_references
- find_implementations
- find_callers
- find_overrides
- find_dead_code
- get_symbol_detail
- get_public_api
- get_type_hierarchy
- get_project_graph
- get_dependency_graph
- get_diagnostics
- get_test_coverage_map
- detect_antipatterns
- detect_circular_dependencies

Prefer MCP tools before reading source.

---

# Repository Overview

This repository is a cryptocurrency statistical arbitrage trading system.

Major domains:

- cross-sectional alpha research
- beta-neutral portfolio construction
- feature engineering
- exchange data collection
- backtesting
- machine learning workflows

---

# Python Environment Rules

All Python work must use:

`conda activate lean`

Rules:

- Never create `venv`
- Never create `virtualenv`
- Never create `poetry` environments

Before running Python:

1. Verify active environment
2. Activate `lean` if needed
3. Follow repository-consistent execution patterns

---

# Implementation Guidelines

## Scope Discipline

- Only implement what the user explicitly requested
- Preserve unrelated functionality
- Do not remove unrelated code
- Avoid formatting-only changes unless required
- Make changes file-by-file
- Provide complete per-file modifications

---

## Code Quality

- Prefer descriptive variable names
- Follow existing repository style
- Prioritize performance
- Prioritize security
- Replace hardcoded values with named constants when appropriate
- Handle edge cases explicitly
- Add assertions when helpful
- Use robust logging and error handling
- Keep implementations modular
- Ensure compatibility with project language/framework versions
- Suggest tests for modified behavior

---

# .NET Rules (Always Applied)

Apply all rules from:

- rules/dotnet/coding-style.md
- rules/dotnet/architecture.md
- rules/dotnet/security.md
- rules/dotnet/testing.md
- rules/dotnet/performance.md
- rules/dotnet/error-handling.md
- rules/dotnet/git-workflow.md
- rules/dotnet/agents.md
- rules/dotnet/hooks.md

---

# Repository-specific Performance Rules

This is a trading system.

Performance is critical.

Always:

- minimize allocations
- prefer streaming over buffering
- avoid unnecessary copies
- prefer Span<T> / Memory<T> where applicable
- prefer pooling where applicable
- avoid blocking I/O in hot paths
- avoid expensive reflection in runtime loops
- avoid unnecessary serialization

For Python:

- prefer vectorized operations
- avoid row-by-row loops unless unavoidable

---

# Verification Rules

Before presenting conclusions:

- verify assumptions against actual code
- verify file paths
- verify dependencies
- verify implementations

Never rely on guessed structure.
