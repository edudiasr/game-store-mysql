-- Loja de um servidor de jogo: pacotes de moedas, VIP e kits.
-- MySQL 8.0+ (usa CHECK, JSON_TABLE e índices funcionais).

CREATE TABLE customers (
    id          BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    steam_id    CHAR(17)     NOT NULL,
    nickname    VARCHAR(64)  NOT NULL,
    email       VARCHAR(254) NULL,
    created_at  DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    CONSTRAINT uq_customers_steam UNIQUE (steam_id),
    CONSTRAINT uq_customers_email UNIQUE (email),
    CONSTRAINT ck_customers_steam CHECK (steam_id REGEXP '^[0-9]{17}$')
) ENGINE = InnoDB;

CREATE TABLE products (
    id           INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    sku          VARCHAR(32)  NOT NULL,
    name         VARCHAR(100) NOT NULL,
    kind         ENUM('coins', 'vip', 'kit') NOT NULL,
    price_cents  INT UNSIGNED NOT NULL,
    coins        INT UNSIGNED NOT NULL DEFAULT 0,   -- moedas que o pacote credita
    stock        INT UNSIGNED NULL,                 -- NULL = sem limite
    active       BOOLEAN      NOT NULL DEFAULT TRUE,
    CONSTRAINT uq_products_sku UNIQUE (sku),
    CONSTRAINT ck_products_price CHECK (price_cents > 0),
    CONSTRAINT ck_products_coins CHECK (kind <> 'coins' OR coins > 0)
) ENGINE = InnoDB;

CREATE TABLE coupons (
    code         VARCHAR(32)      NOT NULL PRIMARY KEY,
    percent_off  TINYINT UNSIGNED NOT NULL,
    max_uses     INT UNSIGNED     NULL,             -- NULL = sem limite
    used         INT UNSIGNED     NOT NULL DEFAULT 0,
    expires_at   DATETIME(3)      NULL,
    CONSTRAINT ck_coupons_percent CHECK (percent_off BETWEEN 1 AND 90),
    CONSTRAINT ck_coupons_used CHECK (max_uses IS NULL OR used <= max_uses)
) ENGINE = InnoDB;

CREATE TABLE orders (
    id              BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    customer_id     BIGINT UNSIGNED NOT NULL,
    status          ENUM('pending', 'paid', 'cancelled') NOT NULL DEFAULT 'pending',
    subtotal_cents  INT UNSIGNED NOT NULL,
    discount_cents  INT UNSIGNED NOT NULL DEFAULT 0,
    total_cents     INT UNSIGNED NOT NULL,
    coupon_code     VARCHAR(32)  NULL,
    created_at      DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    paid_at         DATETIME(3)  NULL,
    CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (id),
    CONSTRAINT fk_orders_coupon FOREIGN KEY (coupon_code) REFERENCES coupons (code),
    CONSTRAINT ck_orders_total CHECK (total_cents = subtotal_cents - discount_cents),
    CONSTRAINT ck_orders_paid CHECK ((status = 'paid') = (paid_at IS NOT NULL)),
    INDEX ix_orders_customer (customer_id, created_at),
    INDEX ix_orders_status (status, created_at)
) ENGINE = InnoDB;

CREATE TABLE order_items (
    order_id          BIGINT UNSIGNED   NOT NULL,
    product_id        INT UNSIGNED      NOT NULL,
    quantity          SMALLINT UNSIGNED NOT NULL,
    unit_price_cents  INT UNSIGNED      NOT NULL,   -- preço congelado no momento da compra
    PRIMARY KEY (order_id, product_id),
    CONSTRAINT fk_items_order FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE CASCADE,
    CONSTRAINT fk_items_product FOREIGN KEY (product_id) REFERENCES products (id),
    CONSTRAINT ck_items_quantity CHECK (quantity BETWEEN 1 AND 100),
    INDEX ix_items_product (product_id)
) ENGINE = InnoDB;

CREATE TABLE payments (
    id            BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    order_id      BIGINT UNSIGNED NOT NULL,
    provider      VARCHAR(20)  NOT NULL,
    provider_ref  VARCHAR(64)  NOT NULL,            -- id do pagamento no provedor
    amount_cents  INT UNSIGNED NOT NULL,
    received_at   DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    CONSTRAINT fk_payments_order FOREIGN KEY (order_id) REFERENCES orders (id),
    CONSTRAINT uq_payments_order UNIQUE (order_id),
    CONSTRAINT uq_payments_ref UNIQUE (provider, provider_ref)
) ENGINE = InnoDB;

-- Carteira de moedas no formato de livro-razão: o saldo é a soma dos lançamentos,
-- nada é editado nem apagado.
CREATE TABLE wallet_entries (
    id           BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    customer_id  BIGINT UNSIGNED NOT NULL,
    amount       INT          NOT NULL,             -- positivo credita, negativo debita
    reason       ENUM('purchase', 'spend', 'refund', 'adjust') NOT NULL,
    order_id     BIGINT UNSIGNED NULL,
    created_at   DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    CONSTRAINT fk_wallet_customer FOREIGN KEY (customer_id) REFERENCES customers (id),
    CONSTRAINT fk_wallet_order FOREIGN KEY (order_id) REFERENCES orders (id),
    CONSTRAINT ck_wallet_amount CHECK (amount <> 0),
    INDEX ix_wallet_customer (customer_id, id)
) ENGINE = InnoDB;
