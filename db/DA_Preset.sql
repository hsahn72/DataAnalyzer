-- DataAnalyzer 팀 공유 프리셋 테이블.
-- 앱이 처음 연결할 때 자동으로 만들지만, 사용자에게 CREATE TABLE 권한이 없으면 DBA가 이 스크립트를 한 번 실행하세요.
-- 사용자에게 필요한 권한: dbo.DA_Preset 에 SELECT, INSERT, UPDATE, DELETE.
-- Kind: chart / excavator / wheelloader / excavator_inertial,  Json: 프리셋 내용(JSON)

IF OBJECT_ID(N'dbo.DA_Preset', N'U') IS NULL
CREATE TABLE dbo.DA_Preset (
    Kind      NVARCHAR(40)  NOT NULL,
    Name      NVARCHAR(200) NOT NULL,
    Json      NVARCHAR(MAX) NOT NULL,
    UpdatedBy NVARCHAR(100) NULL,
    UpdatedAt DATETIME2(0)  NOT NULL CONSTRAINT DF_DA_Preset_UpdatedAt DEFAULT SYSDATETIME(),
    CONSTRAINT PK_DA_Preset PRIMARY KEY (Kind, Name)
);
