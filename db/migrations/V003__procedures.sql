-- Regras de negócio que precisam ser atômicas ficam no banco, dentro de transação.
-- Erros de regra saem como SQLSTATE 45000 com a mensagem, e a API traduz pra HTTP.

-- Cria um pedido a partir de um carrinho em JSON: [{"sku": "COINS_1000", "qty": 2}, ...]
CREATE PROCEDURE sp_place_order(
    IN  p_steam_id CHAR(17),
    IN  p_items    JSON,
    IN  p_coupon   VARCHAR(32),
    OUT p_order_id BIGINT UNSIGNED
)
BEGIN
    DECLARE v_customer  BIGINT UNSIGNED DEFAULT NULL;
    DECLARE v_lines     INT DEFAULT 0;
    DECLARE v_found     INT DEFAULT 0;
    DECLARE v_short     INT DEFAULT 0;
    DECLARE v_subtotal  INT UNSIGNED DEFAULT 0;
    DECLARE v_percent   TINYINT UNSIGNED DEFAULT NULL;
    DECLARE v_discount  INT UNSIGNED DEFAULT 0;

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        DROP TEMPORARY TABLE IF EXISTS tmp_cart;
        RESIGNAL;
    END;

    SELECT id INTO v_customer FROM customers WHERE steam_id = p_steam_id;
    IF v_customer IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'cliente não encontrado';
    END IF;

    DROP TEMPORARY TABLE IF EXISTS tmp_cart;
    CREATE TEMPORARY TABLE tmp_cart (sku VARCHAR(32) PRIMARY KEY, qty INT NOT NULL);
    -- o mesmo sku repetido no carrinho vira uma linha só
    INSERT INTO tmp_cart (sku, qty)
    SELECT j.sku, SUM(j.qty)
    FROM JSON_TABLE(p_items, '$[*]' COLUMNS (
        sku VARCHAR(32) PATH '$.sku' ERROR ON EMPTY,
        qty INT         PATH '$.qty' ERROR ON EMPTY
    )) AS j
    GROUP BY j.sku;

    SELECT COUNT(*) INTO v_lines FROM tmp_cart;
    IF v_lines = 0 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'carrinho vazio';
    END IF;
    IF EXISTS (SELECT 1 FROM tmp_cart WHERE qty < 1 OR qty > 100) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'quantidade inválida (1 a 100)';
    END IF;

    START TRANSACTION;

    -- Trava as linhas dos produtos do carrinho. Uma segunda compra do mesmo produto
    -- fica esperando aqui até esta terminar, e aí já enxerga o estoque atualizado.
    SELECT COUNT(*) INTO v_found
    FROM products p JOIN tmp_cart c ON c.sku = p.sku
    WHERE p.active
    FOR UPDATE OF p;

    IF v_found <> v_lines THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'produto inexistente ou fora de venda';
    END IF;

    SELECT COUNT(*) INTO v_short
    FROM products p JOIN tmp_cart c ON c.sku = p.sku
    WHERE p.stock IS NOT NULL AND p.stock < c.qty
    FOR UPDATE OF p;

    IF v_short > 0 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'estoque insuficiente';
    END IF;

    SELECT SUM(p.price_cents * c.qty) INTO v_subtotal
    FROM products p JOIN tmp_cart c ON c.sku = p.sku
    FOR UPDATE OF p;

    IF p_coupon IS NOT NULL THEN
        SELECT percent_off INTO v_percent
        FROM coupons
        WHERE code = p_coupon
          AND (expires_at IS NULL OR expires_at > NOW(3))
          AND (max_uses IS NULL OR used < max_uses)
        FOR UPDATE;
        IF v_percent IS NULL THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'cupom inválido, vencido ou esgotado';
        END IF;
        UPDATE coupons SET used = used + 1 WHERE code = p_coupon;
        SET v_discount = FLOOR(v_subtotal * v_percent / 100);
    END IF;

    INSERT INTO orders (customer_id, subtotal_cents, discount_cents, total_cents, coupon_code)
    VALUES (v_customer, v_subtotal, v_discount, v_subtotal - v_discount, p_coupon);
    SET p_order_id = LAST_INSERT_ID();

    INSERT INTO order_items (order_id, product_id, quantity, unit_price_cents)
    SELECT p_order_id, p.id, c.qty, p.price_cents
    FROM products p JOIN tmp_cart c ON c.sku = p.sku;

    UPDATE products p JOIN tmp_cart c ON c.sku = p.sku
    SET p.stock = p.stock - c.qty
    WHERE p.stock IS NOT NULL;

    COMMIT;
    DROP TEMPORARY TABLE tmp_cart;
END;
-- @@
-- Confirma o pagamento que chegou pelo webhook do provedor.
-- Idempotente: o provedor reenvia a mesma notificação quando não recebe resposta a tempo,
-- e a segunda chamada não pode creditar as moedas de novo.
CREATE PROCEDURE sp_confirm_payment(
    IN  p_order_id BIGINT UNSIGNED,
    IN  p_provider VARCHAR(20),
    IN  p_ref      VARCHAR(64),
    IN  p_amount   INT UNSIGNED,
    OUT p_result   VARCHAR(16)
)
proc: BEGIN
    DECLARE v_status    VARCHAR(10) DEFAULT NULL;
    DECLARE v_total     INT UNSIGNED;
    DECLARE v_customer  BIGINT UNSIGNED;
    DECLARE v_existing  BIGINT UNSIGNED DEFAULT NULL;

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;

    START TRANSACTION;

    -- trava o pedido primeiro: duas notificações iguais ao mesmo tempo ficam em fila
    SELECT status, total_cents, customer_id INTO v_status, v_total, v_customer
    FROM orders WHERE id = p_order_id
    FOR UPDATE;

    IF v_status IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'pedido não encontrado';
    END IF;

    SELECT order_id INTO v_existing
    FROM payments WHERE provider = p_provider AND provider_ref = p_ref;

    IF v_existing IS NOT NULL THEN
        IF v_existing <> p_order_id THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'esse pagamento já foi usado em outro pedido';
        END IF;
        COMMIT;
        SET p_result = 'duplicate';
        LEAVE proc;
    END IF;

    IF v_status <> 'pending' THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'pedido não está aguardando pagamento';
    END IF;
    IF p_amount <> v_total THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'valor pago diferente do total do pedido';
    END IF;

    INSERT INTO payments (order_id, provider, provider_ref, amount_cents)
    VALUES (p_order_id, p_provider, p_ref, p_amount);

    UPDATE orders SET status = 'paid', paid_at = NOW(3) WHERE id = p_order_id;

    -- pacotes de moedas creditam na carteira
    INSERT INTO wallet_entries (customer_id, amount, reason, order_id)
    SELECT v_customer, SUM(i.quantity * p.coins), 'purchase', p_order_id
    FROM order_items i JOIN products p ON p.id = i.product_id
    WHERE i.order_id = p_order_id AND p.kind = 'coins'
    HAVING SUM(i.quantity * p.coins) > 0;

    COMMIT;
    SET p_result = 'paid';
END;
-- @@
-- Cancela um pedido que ainda não foi pago e devolve estoque e uso do cupom.
CREATE PROCEDURE sp_cancel_order(IN p_order_id BIGINT UNSIGNED)
BEGIN
    DECLARE v_status VARCHAR(10) DEFAULT NULL;
    DECLARE v_coupon VARCHAR(32) DEFAULT NULL;

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;

    START TRANSACTION;

    SELECT status, coupon_code INTO v_status, v_coupon
    FROM orders WHERE id = p_order_id
    FOR UPDATE;

    IF v_status IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'pedido não encontrado';
    END IF;
    IF v_status <> 'pending' THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'só dá pra cancelar pedido pendente';
    END IF;

    UPDATE products p JOIN order_items i ON i.product_id = p.id
    SET p.stock = p.stock + i.quantity
    WHERE i.order_id = p_order_id AND p.stock IS NOT NULL;

    IF v_coupon IS NOT NULL THEN
        UPDATE coupons SET used = used - 1 WHERE code = v_coupon;
    END IF;

    UPDATE orders SET status = 'cancelled' WHERE id = p_order_id;
    COMMIT;
END;
-- @@
-- Gasta moedas da carteira (comprar item dentro do jogo, por exemplo).
-- Trava a linha do cliente pra dois gastos ao mesmo tempo não deixarem o saldo negativo.
CREATE PROCEDURE sp_spend_coins(
    IN  p_steam_id CHAR(17),
    IN  p_amount   INT UNSIGNED,
    OUT p_balance  INT
)
BEGIN
    DECLARE v_customer BIGINT UNSIGNED DEFAULT NULL;
    DECLARE v_balance  INT DEFAULT 0;

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;

    IF p_amount = 0 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'valor precisa ser maior que zero';
    END IF;

    START TRANSACTION;

    SELECT id INTO v_customer FROM customers WHERE steam_id = p_steam_id FOR UPDATE;
    IF v_customer IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'cliente não encontrado';
    END IF;

    SELECT COALESCE(SUM(amount), 0) INTO v_balance FROM wallet_entries WHERE customer_id = v_customer;
    IF v_balance < p_amount THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'saldo insuficiente';
    END IF;

    INSERT INTO wallet_entries (customer_id, amount, reason) VALUES (v_customer, -CAST(p_amount AS SIGNED), 'spend');
    SET p_balance = v_balance - p_amount;
    COMMIT;
END;
