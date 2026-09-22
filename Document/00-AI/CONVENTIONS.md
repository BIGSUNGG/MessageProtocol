---
project: DS_MessageProtocol
type: context
status: stable
tags: [ai, conventions]
updated: 2026-09-22
---

# Conventions

이 Document vault와 코드 문서화에 공통으로 적용한다. 세 DS 프로젝트 Document 구조는 동일하다.

## Vault 구조

| 폴더 | 역할 |
| ------ | ------ |
| `00-AI/` | AI·에이전트 진입점 |
| `01-Overview/` | 사람용 Home / 범위 |
| `02-Architecture/` | 구조·컴포넌트·데이터 흐름·스펙 |
| `03-Reference/` | 패키지·API·설정 레퍼런스 |
| `04-Guides/` | 시작·How-To |
| `05-Decisions/` | ADR |
| `06-Troubleshooting/` | FAQ·장애 |
| `_meta/` | Changelog 등 메타 |

## Frontmatter

```yaml
---
project: DS_MessageProtocol
type: context|overview|architecture|reference|guide|adr|troubleshoot
status: stub|draft|stable
tags: []
updated: YYYY-MM-DD
---
```

## 링크

- 같은 vault 안 문서끼리는 상대 경로 마크다운 링크 사용 (예: `[CONTEXT](./CONTEXT.md)`)
- 한 개념 = 한 파일
- AI는 [CONTEXT](./CONTEXT.md)에서 시작해 링크를 따라간다

## ADR

- 파일명: `NNNN-short-title.md` (예: `0001-Rewrite-Bootstrap.md`)
- Status / Context / Decision / Consequences 섹션 필수

## 작성 상태

- `stub`: 섹션만 있는 자리표시
- `draft`: 초안, 사실 검증 필요
- `stable`: 합의된 내용

## 코드 주석

- 모든 코드 주석(`//` 및 `///` XML doc)은 **영어로 작성**한다. 라이브러리 소비자가 IntelliSense·예제·테스트에서 그대로 읽는 텍스트이기 때문이다. 배경: [ADR-0008](../05-Decisions/ADR-0008-English-Code-Comments.md)
- `///`는 소비자 대상 API 계약 — `<summary>` 첫 줄은 완결된 한 문장으로 동작을 서술하고, 예외·경계 동작을 문서화한다.
- `//`는 코드가 "무엇을" 하는지의 반복이 아니라 "왜" 그렇게 하는지(의도·제약·함정)를 설명한다.
- 예외: UTF-8/비ASCII 직렬화를 검증하는 테스트 페이로드 문자열(예: `"한글·日本語·🌟"`)은 데이터이므로 유지하며, 옆에 의도를 밝히는 영어 주석을 붙인다.
- `Document/` 볼트 작성 언어는 이 규약의 대상이 아니다(한국어 유지 — 상단 원칙 참조).

## 관련

- [CONTEXT](./CONTEXT.md)
- [Home](../01-Overview/Home.md)
