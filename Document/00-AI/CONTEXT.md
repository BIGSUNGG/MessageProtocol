---
project: DS_MessageProtocol
type: context
status: stable
tags: [ai, context]
updated: 2026-09-13
---

# CONTEXT — DS_MessageProtocol

> **AI: 이 vault를 다룰 때 먼저 이 파일을 읽는다.**

## 한 줄 요약

컴파일 타임 메시지 직렬화와 런타임 MessageSerializer를 제공하는 .NET 라이브러리. Unity / .NET Standard 2.1. v2 재작성 완료(2026-08-31).

## 저장소

- GitHub: <https://github.com/BIGSUNGG/DS_MessageProtocol>
- 문서 vault 루트: `Document/` (이 폴더가 Obsidian Vault)
- 솔루션: `MessageProtocol.sln`
- 제품: `Source/` (Core · CodeGenerator · MessageProtocol · Shared)
- 테스트: `Test/` (MessageProtocol.Tests `net8.0;net9.0` · NetStandardFixtures `netstandard2.1` — `CollectionsMarshal` 이 없는 Unity 호환 프로필에서 생성된 폴백 코드를 실행으로 검증하는 픽스처 어셈블리 · Benchmarks `net9.0`)
- 인수 조건: `Sandbox/MessageProtocol.Sandbox` (실행 시나리오, 통과 시 종료 코드 0)
- 참조 구현: `Legacy/` (v1 전체 + 구 문서 `Legacy/Document/`)

## 읽을 순서

1. CONTEXT (지금)
2. [GLOSSARY](./GLOSSARY.md)
3. [Feature-Spec](../02-Architecture/Feature-Spec.md) — 지원 기능 스펙 (F1–F10)
4. [Overview](../02-Architecture/Overview.md) (Architecture)
5. [Packages](../03-Reference/Packages.md) · [Public-API](../03-Reference/Public-API.md)
6. `05-Decisions/` ADR ([ADR-0001](../05-Decisions/ADR-0001-Rewrite-Bootstrap.md))
7. [CONVENTIONS](./CONVENTIONS.md)

## 패키지 요약 (3.0.0)

| NuGet 패키지 | 설명 |
| -------------- | ------ |
| **MessageProtocol** | 메인 패키지. Core·CodeGenerator 를 NuGet 의존성으로 함께 설치 |
| **MessageProtocol.Core** | 직렬화 런타임 API (`MessageSerializer`, 메시지 계약) |
| **MessageProtocol.CodeGenerator** | Roslyn 분석기/소스 생성기 (고급·세분화 참조용, netstandard2.0) |

## 형제 프로젝트

- DS_Communication — 네트워크 전송
- DS_MessageProtocol — 메시지 직렬화 (이 저장소)
- DS_RPC — 분산 RPC (위 둘에 의존)

의존 방향: **DS_RPC → DS_MessageProtocol, DS_Communication**

## 관련 노트

- 사람용 시작: [Home](../01-Overview/Home.md)
- 스펙: [Feature-Spec](../02-Architecture/Feature-Spec.md)
- 구조: [Overview](../02-Architecture/Overview.md)
- 규칙: [CONVENTIONS](./CONVENTIONS.md)
