"""Runtime knobs, read from .env. Every field has a default, so the demo runs unconfigured."""

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", env_file_encoding="utf-8")

    host: str = "0.0.0.0"
    port: int = 8100
    api_token: str = "demo-token-change-me"
    db_path: str = "./demo.db"
    vendor_count: int = 60
    part_count: int = 400
    purchase_order_count: int = 2000


settings = Settings()
