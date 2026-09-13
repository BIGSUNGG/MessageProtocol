---
project: DS_MessageProtocol
type: adr
status: accepted
tags: [adr, harness, pi, ai-collaboration]
updated: 2026-09-14
---

# ADR 0007: 하네스 전환 — 기존 하네스 제거, UniNet PI 하네스 도입

## Status

Accepted (2026-09-14)

## Context

이 저장소는 그동안 `.pi-glla/`(감사 루프 원장·아카이브), `.pi-subagents/`(서브에이전트 산출물), `.cursor/`(Cursor 전용 에이전트·훅·[ds-document-vault 스킬](../../.cursor) — 삭제됨)로 이루어진 구 하네스로 AI 협업을 운영해 왔다. 구성이 도구별로 분산되어 같은 규칙(문서 동기화, 리뷰 루프)이 여러 곳에 중복 정의됐고, 형제 리포 UniNet은 `.pi/`(pi 하네스) 단일 구성으로 정리되어 운영 중이었다. 하네스를 DS 계열 전체에서 하나로 통일할 필요가 있었다. 이번 전환과 동일한 결정이 형제 리포 DS_Communication([ADR 0010](타 저장소 문서, 링크 불가))과 DS_RPC에도 적용된다.

## Decision

1. **구 하네스 전부 제거** — `.pi-glla/`, `.pi-subagents/`, `.cursor/` 디렉터리 삭제. 구 워크플로(`ds-document-vault`) 기반 `AGENTS.md`는 폐기하고 새 형식으로 전면 교체. 문서 속 구 하네스 참조(감사 루프 원장 언급 등)도 정리.
2. **UniNet PI 하네스와 동일 구성 채택** — `.pi/agents/reviewer.md`(읽기 전용 리뷰어, CLEAN/ISSUES 판정), `.pi/extensions/doc-guard.ts`(문서 미갱신 감시 훅), `.pi/skills/` 5종(doc-sync·review-until-clean·review-structure·review-security·review-performance)을 UniNet 원본과 동일하게 둔다. reviewer·review-* 스킬·doc-guard는 UniNet 내용을 그대로 유지하되 저장소명 치환과 문서 경로 매핑(`Document/conventions.md` → `Document/00-AI/CONVENTIONS.md` 등)만 적용했다.
3. **문서 사용법은 기존 Vault 구조에 맞게 적응** — 이 저장소의 `Document/`는 번호 폴더 구조(`00-AI/`·`01-Overview/`·`02-Architecture/`·`03-Reference/`·`05-Decisions/`·`06-Troubleshooting/`·`_meta/`)를 유지한다. 따라서 doc-sync 스킬·AGENTS.md의 문서 원칙·하네스 구성표([HARNESS](../00-AI/HARNESS.md) 신설)는 이 구조를 읽고 쓰도록 재작성했다. UniNet의 플랜 구조(`00-INDEX.md`·`features/`·`_templates/`)로 Vault를 개편하지 않는다.
4. **Vault 관행 계승** — YAML frontmatter(`updated`/`status`) 갱신, 링크 표기, `_meta/Changelog.md` 날짜 그룹 형식은 기존 관행을 그대로 따른다.

## Consequences

- 삭제: `.cursor/`(트랙 17파일), `.pi-glla/`, `.pi-subagents/`(비트랙), 구 `AGENTS.md`.
- 신설: `.pi/` 7파일, [HARNESS](../00-AI/HARNESS.md), 본 ADR.
- 코드·테스트·기존 Vault 문서 구조는 변경 없음. 하네스 관련 문서(HARNESS·doc-sync)만 신설·교체.
