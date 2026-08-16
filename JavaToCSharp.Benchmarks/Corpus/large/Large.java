package com.example.large;

import java.util.List;
import java.util.ArrayList;
import java.util.Map;
import java.util.HashMap;

public class Large {
    private static final int COUNT = 40;

    public static void main(String[] args) {
        List<Entity> entities = new ArrayList<>();
        for (int i = 0; i < COUNT; i++) {
            entities.add(EntityFactory.create("entity-" + i, i));
        }
        double total = entities.stream().mapToDouble(Entity::score).sum();
        System.out.println("total=" + total);
    }
}

interface Processor0 {
    int process0(int input);
}

interface Processor1 {
    int process1(int input);
}

interface Processor2 {
    int process2(int input);
}

interface Processor3 {
    int process3(int input);
}

interface Processor4 {
    int process4(int input);
}

interface Processor5 {
    int process5(int input);
}

interface Processor6 {
    int process6(int input);
}

interface Processor7 {
    int process7(int input);
}

interface Processor8 {
    int process8(int input);
}

interface Processor9 {
    int process9(int input);
}

interface Processor10 {
    int process10(int input);
}

interface Processor11 {
    int process11(int input);
}

enum Status0 {
    IDLE0, RUNNING0, DONE0;

    public boolean isDone0() {
        return this == DONE0;
    }
}

enum Status1 {
    IDLE1, RUNNING1, DONE1;

    public boolean isDone1() {
        return this == DONE1;
    }
}

enum Status2 {
    IDLE2, RUNNING2, DONE2;

    public boolean isDone2() {
        return this == DONE2;
    }
}

enum Status3 {
    IDLE3, RUNNING3, DONE3;

    public boolean isDone3() {
        return this == DONE3;
    }
}

enum Status4 {
    IDLE4, RUNNING4, DONE4;

    public boolean isDone4() {
        return this == DONE4;
    }
}

enum Status5 {
    IDLE5, RUNNING5, DONE5;

    public boolean isDone5() {
        return this == DONE5;
    }
}

enum Status6 {
    IDLE6, RUNNING6, DONE6;

    public boolean isDone6() {
        return this == DONE6;
    }
}

enum Status7 {
    IDLE7, RUNNING7, DONE7;

    public boolean isDone7() {
        return this == DONE7;
    }
}

enum Status8 {
    IDLE8, RUNNING8, DONE8;

    public boolean isDone8() {
        return this == DONE8;
    }
}

enum Status9 {
    IDLE9, RUNNING9, DONE9;

    public boolean isDone9() {
        return this == DONE9;
    }
}

enum Status10 {
    IDLE10, RUNNING10, DONE10;

    public boolean isDone10() {
        return this == DONE10;
    }
}

enum Status11 {
    IDLE11, RUNNING11, DONE11;

    public boolean isDone11() {
        return this == DONE11;
    }
}

public class Entity0 {
    private final String name0;
    private final int value0;
    private double score0;

    public Entity0(String name, int value) {
        this.name0 = name;
        this.value0 = value;
        this.score0 = value * 1.5;
    }

    public double score() {
        return score0;
    }

    public String describe() {
        return name0 + ":" + value0;
    }
}

public class Entity1 {
    private final String name1;
    private final int value1;
    private double score1;

    public Entity1(String name, int value) {
        this.name1 = name;
        this.value1 = value;
        this.score1 = value * 1.5;
    }

    public double score() {
        return score1;
    }

    public String describe() {
        return name1 + ":" + value1;
    }
}

public class Entity2 {
    private final String name2;
    private final int value2;
    private double score2;

    public Entity2(String name, int value) {
        this.name2 = name;
        this.value2 = value;
        this.score2 = value * 1.5;
    }

    public double score() {
        return score2;
    }

    public String describe() {
        return name2 + ":" + value2;
    }
}

public class Entity3 {
    private final String name3;
    private final int value3;
    private double score3;

    public Entity3(String name, int value) {
        this.name3 = name;
        this.value3 = value;
        this.score3 = value * 1.5;
    }

    public double score() {
        return score3;
    }

    public String describe() {
        return name3 + ":" + value3;
    }
}

public class Entity4 {
    private final String name4;
    private final int value4;
    private double score4;

    public Entity4(String name, int value) {
        this.name4 = name;
        this.value4 = value;
        this.score4 = value * 1.5;
    }

    public double score() {
        return score4;
    }

    public String describe() {
        return name4 + ":" + value4;
    }
}

public class Entity5 {
    private final String name5;
    private final int value5;
    private double score5;

    public Entity5(String name, int value) {
        this.name5 = name;
        this.value5 = value;
        this.score5 = value * 1.5;
    }

    public double score() {
        return score5;
    }

    public String describe() {
        return name5 + ":" + value5;
    }
}

public class Entity6 {
    private final String name6;
    private final int value6;
    private double score6;

    public Entity6(String name, int value) {
        this.name6 = name;
        this.value6 = value;
        this.score6 = value * 1.5;
    }

    public double score() {
        return score6;
    }

    public String describe() {
        return name6 + ":" + value6;
    }
}

public class Entity7 {
    private final String name7;
    private final int value7;
    private double score7;

    public Entity7(String name, int value) {
        this.name7 = name;
        this.value7 = value;
        this.score7 = value * 1.5;
    }

    public double score() {
        return score7;
    }

    public String describe() {
        return name7 + ":" + value7;
    }
}

public class Entity8 {
    private final String name8;
    private final int value8;
    private double score8;

    public Entity8(String name, int value) {
        this.name8 = name;
        this.value8 = value;
        this.score8 = value * 1.5;
    }

    public double score() {
        return score8;
    }

    public String describe() {
        return name8 + ":" + value8;
    }
}

public class Entity9 {
    private final String name9;
    private final int value9;
    private double score9;

    public Entity9(String name, int value) {
        this.name9 = name;
        this.value9 = value;
        this.score9 = value * 1.5;
    }

    public double score() {
        return score9;
    }

    public String describe() {
        return name9 + ":" + value9;
    }
}

public class Entity10 {
    private final String name10;
    private final int value10;
    private double score10;

    public Entity10(String name, int value) {
        this.name10 = name;
        this.value10 = value;
        this.score10 = value * 1.5;
    }

    public double score() {
        return score10;
    }

    public String describe() {
        return name10 + ":" + value10;
    }
}

public class Entity11 {
    private final String name11;
    private final int value11;
    private double score11;

    public Entity11(String name, int value) {
        this.name11 = name;
        this.value11 = value;
        this.score11 = value * 1.5;
    }

    public double score() {
        return score11;
    }

    public String describe() {
        return name11 + ":" + value11;
    }
}

public class Entity12 {
    private final String name12;
    private final int value12;
    private double score12;

    public Entity12(String name, int value) {
        this.name12 = name;
        this.value12 = value;
        this.score12 = value * 1.5;
    }

    public double score() {
        return score12;
    }

    public String describe() {
        return name12 + ":" + value12;
    }
}

public class Entity13 {
    private final String name13;
    private final int value13;
    private double score13;

    public Entity13(String name, int value) {
        this.name13 = name;
        this.value13 = value;
        this.score13 = value * 1.5;
    }

    public double score() {
        return score13;
    }

    public String describe() {
        return name13 + ":" + value13;
    }
}

public class Entity14 {
    private final String name14;
    private final int value14;
    private double score14;

    public Entity14(String name, int value) {
        this.name14 = name;
        this.value14 = value;
        this.score14 = value * 1.5;
    }

    public double score() {
        return score14;
    }

    public String describe() {
        return name14 + ":" + value14;
    }
}

public class Entity15 {
    private final String name15;
    private final int value15;
    private double score15;

    public Entity15(String name, int value) {
        this.name15 = name;
        this.value15 = value;
        this.score15 = value * 1.5;
    }

    public double score() {
        return score15;
    }

    public String describe() {
        return name15 + ":" + value15;
    }
}

public final class EntityFactory {
    private EntityFactory() { }

    public static Object create(String name, int value) {
        switch (value % 4) {
            case 0: return new Entity0(name, value);
            case 1: return new Entity1(name, value);
            case 2: return new Entity2(name, value);
            default: return new Entity3(name, value);
        }
    }
}

