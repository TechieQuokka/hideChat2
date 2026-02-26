# hideChat2

익명 P2P 암호화 채팅 애플리케이션. Tor Hidden Service를 통해 두 사용자가 연결하며, ECDH 키 교환 + AES-256 암호화를 사용합니다.

## 특징

- **완전 익명**: Tor Hidden Service (.onion) 기반 P2P 연결
- **종단간 암호화**: ECDH P-256 키 교환 + AES-256-CBC + HMAC-SHA256
- **Perfect Forward Secrecy**: 세션마다 새로운 에페머럴 키 생성
- **에페머럴 채팅**: 최대 10개 메시지만 표시, 스크롤 없음, 연결 종료 시 앱 자동 종료
- **단일 실행파일**: Costura.Fody로 tor.exe + geoip 파일 포함 빌드

## 요구사항

- Windows 10/11
- .NET Framework 4.8

## 빌드

Visual Studio 2022에서 `hideChat2.slnx` 열고 빌드.

NuGet 패키지 복원이 자동으로 이루어집니다.

## 사용법

1. 앱 실행 → 포트 설정 후 **Tor 시작** 클릭
2. Tor 부트스트랩 완료 (30~60초) → 내 `.onion` 주소 표시
3. 상대방에게 주소 공유 → 상대방이 주소 입력 후 **연결** 클릭
4. 연결 완료 후 채팅 시작

## 아키텍처

```
TorManager      — Tor 프로세스 + Hidden Service 관리
PeerListener    — 수신 측 (Hidden Service 포트 리슨)
PeerConnector   — 발신 측 (SOCKS5 → .onion)
NetworkProtocol — 바이너리 프레임 [Type(1)][Length(4)][Data]
CryptoHelper    — ECDH + AES-256-CBC + HMAC-SHA256
Socks5Client    — SOCKS5 프로토콜 구현
```

## 포트 구조

| 역할 | 포트 |
|------|------|
| SOCKS5 | basePort |
| Control | basePort + 1 |
| Hidden Service | basePort + 2 |

기본값: 9050, 9051, 9052

## 보안 고려사항

- HMAC 검증 실패 시 메시지 폐기 (위변조 방지)
- 상수 시간 비교로 타이밍 공격 방어
- 세션 종료 시 키 메모리 즉시 초기화

## 라이선스

MIT
