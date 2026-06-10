namespace Veil.Zones.Domain.Enums;

public enum ZoneStatus {
    /// <summary>Yeni oluşturuldu, sertifika bekleniyor.</summary>
    Provisioning,

    /// <summary>Aktif — trafik akıyor.</summary>
    Active,

    /// <summary>Manuel durdurma — edge node bypass eder.</summary>
    Paused,

    /// <summary>Hata durumu — sertifika hatası vb.</summary>
    Error
}